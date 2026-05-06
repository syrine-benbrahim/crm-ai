using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Helpers;
using crm_ai.Models;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace crm_ai.Services
{
    // ════════════════════════════════════════════════════════════════════════
    // SELECTION SUGGESTION SERVICE
    //
    // Called after the campaign wizard has collected name + objective + channel.
    //
    // PIPELINE:
    //
    //   Step 1 — Load (C#)
    //     Load up to 50 active selections and their root group rules.
    //     Batch SQL — one query for selections, one for root groups.
    //
    //   Step 2 — Score (C#, zero tokens)
    //     Tier 1: keyword overlap against selection DESCRIPTION (not name).
    //     Tier 2: rule-structure signals (rule categories vs objective intent).
    //     Both tiers run for every candidate.
    //
    //   Step 3 — Threshold check (C#)
    //     If best score >= GoodMatchThreshold → confident match exists.
    //     If best score <  GoodMatchThreshold → no confident match.
    //
    //   Step 4a — Good match path
    //     If multiple candidates tie within TieThreshold points, run AI
    //     tiebreaker (~50 tokens, fast model). Otherwise top scorer wins.
    //     Return ranked suggestions + RecommendedId + HasGoodMatch = true.
    //
    //   Step 4b — No match path
    //     Generate a selection-builder prompt using the fast model (~100 tokens).
    //     This prompt is a natural language string ready to be sent directly
    //     into ContinueConversationAsync as the first user message.
    //     Return suggestions (ranked, none recommended) + SuggestedPrompt
    //     + HasGoodMatch = false.
    //
    // WHY SuggestedPrompt IS THE KEY FEATURE:
    //   The system already knows how to build a selection from natural language.
    //   When no existing selection fits, we don't say "go figure it out" —
    //   we generate the exact prompt the selection builder needs and hand it
    //   back. The frontend puts it in the input box pre-filled, user clicks
    //   once, the existing ContinueConversationAsync flow handles the rest.
    //   Cost: ~100 tokens. Fires only when no good match exists.
    //
    // AI CALL COUNT:
    //   Best case (good match, no tie):  0 AI calls
    //   Good match with tie:             1 AI call, ~50 tokens, fast model
    //   No good match:                   1 AI call, ~100 tokens, fast model
    //   Never more than 1 AI call per suggest request.
    // ════════════════════════════════════════════════════════════════════════

    public class SelectionSuggestionService : ISelectionSuggestionService
    {
        private readonly AppDbContext _context;
        private readonly IAiService _aiService;
        private readonly ILogger<SelectionSuggestionService> _logger;

        // Scores within this band of the top score are considered a tie
        private const int TieThreshold = 3;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        };

        public SelectionSuggestionService(
            AppDbContext context,
            IAiService aiService,
            ILogger<SelectionSuggestionService> logger)
        {
            _context = context;
            _aiService = aiService;
            _logger = logger;
        }

        public async Task<SelectionSuggestionResultDto> SuggestSelectionsAsync(
            string objective,
            string channel,
            string? campaignName = null,
            int maxResults = 5)
        {
            _logger.LogInformation(
                "SuggestSelectionsAsync — objective='{Obj}', channel={Ch}",
                objective.Length > 80 ? objective[..80] + "..." : objective, channel);

            // ── Step 1: Load ─────────────────────────────────────────────────

            var selections = await _context.Selections
                .Where(s => s.Status == "Active")
                .OrderByDescending(s => s.CreatedAt)
                .Take(50)
                .ToListAsync();

            if (selections.Count == 0)
            {
                _logger.LogInformation("No active selections — generating build prompt");
                var prompt = await GenerateSelectionPromptAsync(
                    objective, channel, campaignName);

                return new SelectionSuggestionResultDto
                {
                    HasSelections = false,
                    HasGoodMatch = false,
                    Suggestions = [],
                    SuggestedPrompt = prompt.Prompt,
                    SuggestedPromptExplanation = prompt.Explanation,
                    TokensUsed = prompt.Tokens
                };
            }

            var catalog = await _aiService.BuildNodeCatalogPublicAsync();

            // Batch load root groups — one query, not N queries
            var selectionIds = selections.Select(s => s.Id).ToList();
            var rawRootGroups = await _context.SelectionGroups
                .Include(g => g.Rules)
                .Include(g => g.ChildGroups)
                    .ThenInclude(cg => cg.Rules)
                .Where(g => selectionIds.Contains(g.SelectionId) &&
                            g.ParentGroupId == null)
                .ToListAsync();

            var rootGroupMap = rawRootGroups
                .GroupBy(g => g.SelectionId)
                .ToDictionary(grp => grp.Key, grp => MapGroup(grp.First()));

            // ── Step 2: Score ────────────────────────────────────────────────

            var scored = selections.Select(s =>
            {
                rootGroupMap.TryGetValue(s.Id, out var rootGroup);
                rootGroup ??= EmptyGroup();

                var score = SelectionMatcher.CompositeScore(
                    objective,
                    s.Description,
                    s.Name,
                    rootGroup,
                    catalog);

                return new ScoredSelection
                {
                    Selection = s,
                    RootGroup = rootGroup,
                    Score = score
                };
            })
            .OrderByDescending(x => x.Score)
            .ToList();

            _logger.LogInformation(
                "Scored {Count} selections — top: [{Top}]",
                scored.Count,
                string.Join(", ", scored.Take(5).Select(x =>
                    $"{x.Selection.Name.Split(' ').First()}={x.Score}")));

            // ── Step 3: Threshold check ───────────────────────────────────────

            var bestScore = scored.First().Score;
            bool hasGoodMatch = bestScore >= SelectionMatcher.GoodMatchThreshold;

            // ── Step 4a: Good match path ─────────────────────────────────────

            if (hasGoodMatch)
            {
                var topCandidates = scored
                    .Where(x => x.Score >= bestScore - TieThreshold)
                    .Take(5)
                    .ToList();

                // Run tiebreaker and prompt generation in parallel
                // Prompt always generated — powers the "Build custom" escape hatch
                var promptTask = GenerateSelectionPromptAsync(objective, channel, campaignName);

                int? recommendedId;
                if (topCandidates.Count > 1)
                {
                    _logger.LogInformation(
                        "{Count} selections within tie band — invoking AI tiebreaker",
                        topCandidates.Count);
                    recommendedId = await BreakTieAsync(objective, channel, topCandidates);
                }
                else
                {
                    recommendedId = topCandidates.First().Selection.Id;
                    _logger.LogInformation(
                        "Clear winner: {Name} (score={Score})",
                        topCandidates.First().Selection.Name, bestScore);
                }

                var matchPrompt = await promptTask;

                var suggestions = scored.Take(maxResults).Select(x =>
                {
                    bool isRec = x.Selection.Id == recommendedId;
                    return new SelectionSuggestionDto
                    {
                        Id = x.Selection.Id,
                        Name = x.Selection.Name,
                        Description = x.Selection.Description,
                        Score = x.Score,
                        IsRecommended = isRec,
                        RecommendationReason = isRec
                            ? SelectionMatcher.BuildReason(
                                objective, x.Selection.Description, x.RootGroup, catalog)
                            : null
                    };
                })
                .OrderByDescending(x => x.IsRecommended)
                .ThenByDescending(x => x.Score)
                .ToList();

                return new SelectionSuggestionResultDto
                {
                    HasSelections = true,
                    HasGoodMatch = true,
                    RecommendedId = recommendedId,
                    Suggestions = suggestions,
                    SuggestedPrompt = matchPrompt.Prompt,
                    SuggestedPromptExplanation = matchPrompt.Explanation,
                    TokensUsed = matchPrompt.Tokens
                };
            }

            // ── Step 4b: No good match path ───────────────────────────────────

            _logger.LogInformation(
                "Best score {Score} below threshold {Threshold} — generating build prompt",
                bestScore, SelectionMatcher.GoodMatchThreshold);

            var generatedPrompt = await GenerateSelectionPromptAsync(
                objective, channel, campaignName);

            // Still return scored selections (unranked — none recommended)
            // Frontend can show them as "or choose an existing one" secondary option
            var fallbackSuggestions = scored.Take(maxResults).Select(x =>
                new SelectionSuggestionDto
                {
                    Id = x.Selection.Id,
                    Name = x.Selection.Name,
                    Description = x.Selection.Description,
                    Score = x.Score,
                    IsRecommended = false,
                    RecommendationReason = null
                }).ToList();

            return new SelectionSuggestionResultDto
            {
                HasSelections = true,
                HasGoodMatch = false,
                RecommendedId = null,
                Suggestions = fallbackSuggestions,
                SuggestedPrompt = generatedPrompt.Prompt,
                SuggestedPromptExplanation = generatedPrompt.Explanation,
                TokensUsed = generatedPrompt.Tokens
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PROMPT GENERATION
        //
        // Translates campaign intent into the exact natural language string
        // the selection builder understands.
        //
        // Input:  "Re-engage lapsed loyal customers with a win-back email"
        // Output: "Lapsed or long-term lapsed customers who are loyal or
        //          frequent, emailable, excluding never visited"
        //
        // The output goes directly into ContinueConversationAsync as the
        // first user message — no reformatting needed on the frontend.
        //
        // Uses fast model + two fields:
        //   prompt:      the ready-to-use selection builder input
        //   explanation: one sentence the UI shows to explain why
        //
        // Model choice: fast model — this is short structured output,
        //   not complex reasoning. Same justification as catalog filtering.
        // ════════════════════════════════════════════════════════════════════

        private async Task<(string Prompt, string Explanation, int Tokens)>
            GenerateSelectionPromptAsync(
                string objective,
                string channel,
                string? campaignName)
        {
            const string system =
    """
    You convert a marketing campaign objective into a CRM selection builder prompt.
    A selection builder prompt is a plain-English audience description used to
    build customer filter rules. It must be specific enough to actually build rules.

    RULES:
    - Always include at least ONE of: loyalty tier, recency, spend, age, gender, location
    - If the channel is Email → end with ", emailable"
    - If the channel is SMS → end with ", smsable"
    - If objective mentions recency (months, weeks, days) → include it exactly
    - If objective mentions spend (£X, high value) → include it exactly  
    - If objective mentions loyalty (loyal, lapsed, frequent) → include it exactly
    - If objective is vague with no specifics → default to:
        Email: "active customers who visited in the last month, emailable"
        SMS:   "active customers who visited in the last month, smsable"
    - Never produce fewer than 5 words
    - Never produce just "emailable customers" or "smsable customers" alone

    Use exact terms the selection builder understands:
      Loyalty: loyal, frequent, occasional, lapsed, long-term lapsed
      Recency: last week, last month, 2-3 months, 3-4 months, 4+ months
      Contact: emailable, smsable

          SPEND RULES:
    - Default to "average spend" (per visit) unless user explicitly says 
      "total spend" or "lifetime spend"
    - "spend over £X" → "average spend over £X"
    - "spent over £X" → "average spend over £X"
    - "high spenders" → "average spend over £50"
    - "big spenders" → "average spend over £70"
    - Only use "total spend" if user says "total", "lifetime", "all time"

    Return ONLY valid JSON, no markdown:
    {
      "prompt": "The ready-to-use selection builder sentence",
      "explanation": "One sentence: what audience this will build and why it fits the campaign"
    }

    EXAMPLES:
    Objective: "Reactivate customers who haven't visited in 3 months"
    Channel: SMS
    → {"prompt": "Lapsed or long-term lapsed customers who haven't visited in 2-3 months or 3-4 months, smsable", "explanation": "This will find customers who drifted away 2-4 months ago, reachable by text."}

    Objective: "Target customers who visited on weekends in the last 2 weeks"
    Channel: SMS
    → {"prompt": "Customers who visited in the last 8-14 days, smsable", "explanation": "This will find customers who visited recently in the last two weeks, reachable by text."}

    Objective: "Reward our loyal customers with an exclusive offer"
    Channel: Email
    → {"prompt": "Loyal or frequent customers, emailable", "explanation": "This will find your most engaged customers reachable by email."}

    Objective: "General marketing campaign"
    Channel: Email
    → {"prompt": "Active customers who visited in the last month, emailable", "explanation": "This will find recently active customers reachable by email."}
    """;

            var user =
                $"Campaign name: {campaignName ?? "not specified"}\n" +
                $"Objective: {objective}\n" +
                $"Channel: {channel}\n\n" +
                "Generate the selection builder prompt.";

            try
            {
                var (json, tokens) = await _aiService.CallPublicAsync(
                    system, user,
                    maxTokens: 150,
                    model: _aiService.FastModel);

                _logger.LogInformation(
                    "Prompt generation used {Tokens} tokens", tokens);

                var cleaned = CleanJson(json);
                var parsed = JsonSerializer.Deserialize<JsonElement>(cleaned, _jsonOptions);

                var prompt = parsed.TryGetProperty("prompt", out var p)
                    ? p.GetString() ?? BuildFallbackPrompt(objective, channel)
                    : BuildFallbackPrompt(objective, channel);

                var explanation = parsed.TryGetProperty("explanation", out var e)
                    ? e.GetString() ?? "I'll build a new audience based on your campaign objective."
                    : "I'll build a new audience based on your campaign objective.";

                return (prompt, explanation, tokens);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prompt generation failed — using fallback");
                return (
                    BuildFallbackPrompt(objective, channel),
                    "I'll build a new audience based on your campaign objective.",
                    0
                );
            }
        }

        // Deterministic fallback — never fails, never needs AI
        private static string BuildFallbackPrompt(string objective, string channel)
        {
            var contactFilter = channel.ToLower() switch
            {
                "sms" => ", smsable",
                _ => ", emailable"
            };

            // Simple pass-through with contact filter appended
            // Better than nothing — selection builder can still handle it
            return objective.TrimEnd('.', '!', '?') + contactFilter;
        }

        // ════════════════════════════════════════════════════════════════════
        // TIE BREAKER
        //
        // Only runs when multiple selections score within TieThreshold of each
        // other. Sends objective + short candidate list to fast model.
        // Returns one index. Cost: ~50 tokens.
        //
        // We send descriptions (not names) — descriptions are the semantic
        // content. If no description exists, falls back to name.
        // ════════════════════════════════════════════════════════════════════

        private async Task<int?> BreakTieAsync(
            string objective,
            string channel,
            List<ScoredSelection> candidates)
        {
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0].Selection.Id;

            try
            {
                var list = string.Join("\n", candidates.Select((c, i) =>
                {
                    var text = !string.IsNullOrWhiteSpace(c.Selection.Description)
                        ? c.Selection.Description
                        : c.Selection.Name;
                    return $"{i + 1}. {text}";
                }));

                var (json, tokens) = await _aiService.CallPublicAsync(
                    "Return ONLY valid JSON with no other text: {\"index\": N} " +
                    "where N is the 1-based number of the best matching selection. " +
                    "Do not write any words before or after the JSON object.",
                    $"Objective: \"{objective}\"\nChannel: {channel}\n\n" +
                    $"Selections:\n{list}\n\nReturn {{\"index\": N}}",
                    maxTokens: 20,
                    model: _aiService.FastModel);

                _logger.LogInformation("Tiebreaker used {Tokens} tokens", tokens);

                var s = CleanJson(json);
                var parsed = JsonSerializer.Deserialize<JsonElement>(s, _jsonOptions);

                if (parsed.TryGetProperty("index", out var idx))
                {
                    var i = idx.GetInt32() - 1;
                    if (i >= 0 && i < candidates.Count)
                    {
                        _logger.LogInformation(
                            "Tiebreaker chose: {Name}",
                            candidates[i].Selection.Name);
                        return candidates[i].Selection.Id;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tiebreaker failed — using top scorer");
            }

            return candidates[0].Selection.Id;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static SelectionGroupDto MapGroup(SelectionGroup group) =>
            new()
            {
                LogicalOperator = group.LogicalOperator,
                Rules = group.Rules?.Select(r => new SelectionRuleDto
                {
                    TreeNodeId = r.TreeNodeId,
                    Operator = r.Operator,
                    Value = r.Value
                }).ToList() ?? [],
                Groups = group.ChildGroups?.Select(MapGroup).ToList() ?? []
            };

        private static SelectionGroupDto EmptyGroup() =>
            new() { LogicalOperator = "AND", Rules = [], Groups = [] };

        private static string CleanJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "{}";
            var s = raw.Trim();
            if (s.StartsWith("```json")) s = s[7..];
            else if (s.StartsWith("```")) s = s[3..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
            var start = s.IndexOf('{');
            var end = s.LastIndexOf('}');
            return start >= 0 && end > start ? s[start..(end + 1)] : s;
        }

        private sealed class ScoredSelection
        {
            public Selection Selection { get; set; } = null!;
            public SelectionGroupDto RootGroup { get; set; } = null!;
            public int Score { get; set; }
        }
    }
}