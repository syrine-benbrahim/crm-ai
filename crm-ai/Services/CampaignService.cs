using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Helpers;
using crm_ai.Models;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace crm_ai.Services
{
    public class CampaignService : ICampaignService
    {
        private readonly AppDbContext _context;
        private readonly IAiService _aiService;
        private readonly ILogger<CampaignService> _logger;
        private readonly SegmentProfileBuilder _segmentProfileBuilder;
        private readonly ISqlBuilderService _sqlBuilder;
        private readonly ISelectionSuggestionService _selectionSuggestion;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        };

        public CampaignService(
            AppDbContext context,
            IAiService aiService,
            ILogger<CampaignService> logger,
            SegmentProfileBuilder segmentProfileBuilder,
            ISqlBuilderService sqlBuilder,
            ISelectionSuggestionService selectionSuggestion)
        {
            _context = context;
            _aiService = aiService;
            _logger = logger;
            _segmentProfileBuilder = segmentProfileBuilder;
            _sqlBuilder = sqlBuilder;
            _selectionSuggestion = selectionSuggestion;
        }

        // ════════════════════════════════════════════════════════════════════
        // MAIN CONVERSATION TURN
        // Engineering approach: extract ALL fields simultaneously, track
        // what is missing, ask ONE question, never overwrite confirmed fields
        // ════════════════════════════════════════════════════════════════════

        public async Task<CampaignConversationResponseDto> ContinueCampaignConversationAsync(
            CampaignConversationRequestDto request)
        {
            var draft = request.CurrentDraft ?? new CampaignDraftDto();
            var lastMessage = request.Messages
                .LastOrDefault(m => m.Role == "user")?.Content ?? "";

            _logger.LogInformation(
                "Campaign turn — message='{Msg}', draft={@Draft}, confirmed={C}",
                lastMessage.Length > 50 ? lastMessage[..50] + "..." : lastMessage,
                draft, request.Confirmed);
            if (!string.IsNullOrWhiteSpace(lastMessage))
            {
                var missingBeforeExtraction = GetMissingFields(draft);

                draft = await ExtractAndMergeAsync(lastMessage, draft);
                draft = ValidateDraft(draft);
                if (missingBeforeExtraction.Count == 1
                    && missingBeforeExtraction[0] == "name"
                    && string.IsNullOrWhiteSpace(draft.Name))
                {
                    var candidate = lastMessage.Trim();
                    if (candidate.Length is >= 3 and <= 80
                        && !ValidChannels.Contains(candidate))
                    {
                        draft.Name = candidate;
                    }
                }
            }

            var missing = GetMissingFields(draft);

            _logger.LogInformation(
                "After extraction — missing fields: [{Missing}]",
                string.Join(", ", missing));

            if (request.Confirmed && missing.Count == 0)
            {
                var saved = await SaveCampaignAsync(draft);
                draft.Id = saved.Id;

                var suggestion = await TrySuggestSelectionsAsync(draft);

                return new CampaignConversationResponseDto
                {
                    Status = "completed",
                    Message = BuildCompletedMessage(draft, suggestion),
                    Draft = draft,
                    MissingFields = new(),
                    TokensUsed = suggestion?.TokensUsed ?? 0,
                    SelectionSuggestion = suggestion
                };
            }

            if (missing.Count == 0)
            {
                return new CampaignConversationResponseDto
                {
                    Status = "confirming",
                    Message = PromptTemplates.Campaign.ConfirmationMessage(draft),
                    Draft = draft,
                    MissingFields = new(),
                    TokensUsed = 0
                };
            }

            var (question, tokens, suggestions) = await GetNextQuestionAsync(draft, missing);

            return new CampaignConversationResponseDto
            {
                Status = "collecting",
                Message = question,
                Draft = draft,
                MissingFields = missing,
                TokensUsed = tokens,
                SuggestedNames = suggestions   // new field — see DTO change below
            };
        }
        private async Task<SelectionSuggestionResultDto?> TrySuggestSelectionsAsync(
        CampaignDraftDto draft)
        {
            if (string.IsNullOrWhiteSpace(draft.Objective)) return null;

            try
            {
                return await _selectionSuggestion.SuggestSelectionsAsync(
                    objective: draft.Objective,
                    channel: draft.Channel ?? "Email",
                    campaignName: draft.Name,
                    maxResults: 5);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Selection suggestion failed — campaign still created");
                return null;
            }
        }

        private static string BuildCompletedMessage(
            CampaignDraftDto draft,
            SelectionSuggestionResultDto? suggestion)
        {
            var created = $"Campaign **{draft.Name}** created!";

            if (suggestion == null)
                return created + " Now let's set up your audience.";

            if (!suggestion.HasSelections)
                return created +
                       " You don't have any saved audiences yet — " +
                       "let me build one for you.\n\n" +
                       $"_{suggestion.SuggestedPromptExplanation}_";

            if (suggestion.HasGoodMatch)
            {
                var rec = suggestion.Suggestions.FirstOrDefault(s => s.IsRecommended);
                return created +
                       $" I found **{suggestion.Suggestions.Count}** existing " +
                       $"audience{(suggestion.Suggestions.Count == 1 ? "" : "s")}. " +
                       $"My recommendation is **{rec?.Name}**" +
                       (rec?.RecommendationReason != null
                           ? $" — {rec.RecommendationReason}."
                           : ".") +
                       " Use it, pick another, or I can build a new one.";
            }

          
            return created +
                   " None of your existing audiences are a strong fit. " +
                   "I've prepared a prompt to build the right one:\n\n" +
                   $"_{suggestion.SuggestedPromptExplanation}_\n\n" +
                   "Click **'Build audience'** to create it, or choose an existing one below.";
        }

        public async Task<SelectionSuggestionResultDto> SuggestSelectionsAsync(
        SuggestSelectionsRequestDto request)
        {
            return await _selectionSuggestion.SuggestSelectionsAsync(
                objective: request.Objective,
                channel: request.Channel,
                campaignName: request.CampaignName,
                maxResults: Math.Clamp(request.MaxResults, 1, 10));
        }

        // ════════════════════════════════════════════════════════════════════
        // EXTRACT AND MERGE
        // Fast model — returns 3 fields, short structured output
        // Same model choice as catalog filtering in AiService
        // ════════════════════════════════════════════════════════════════════

        private async Task<CampaignDraftDto> ExtractAndMergeAsync(
            string message, CampaignDraftDto current)
        {
            try
            {
                var (json, _) = await CallFastAsync(
                    PromptTemplates.Campaign.ExtractionSystem,
                    PromptTemplates.Campaign.ExtractionUser(message),
                    maxTokens: 150);

                var extracted = JsonSerializer.Deserialize<CampaignDraftDto>(
                    CleanJson(json), _jsonOptions);

                if (extracted == null) return current;

                return new CampaignDraftDto
                {
                    Id = current.Id,
                    Name = current.Name ?? extracted.Name,
                    Objective = current.Objective ?? extracted.Objective,
                    Channel = current.Channel ?? extracted.Channel,
                    SelectionId = current.SelectionId,
                    SelectionName = current.SelectionName
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Field extraction failed — keeping current draft");
                return current;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // DRAFT VALIDATION — deterministic C#, zero tokens, zero latency
        // Engineering principle: known-value validation never needs AI
        // Same philosophy as SelectionSanitizer — fix bad output deterministically
        // ════════════════════════════════════════════════════════════════════

        private static readonly HashSet<string> ValidChannels = new(
            StringComparer.OrdinalIgnoreCase) { "Email", "SMS", "Push" };

        private static CampaignDraftDto ValidateDraft(CampaignDraftDto draft)
        {
            // Channel validation — only known channels accepted
            // If AI extracted something invalid, null it so the system re-asks
            if (draft.Channel != null && !ValidChannels.Contains(draft.Channel))
                draft.Channel = null;

            // Normalise channel casing
            if (draft.Channel != null)
            {
                draft.Channel = draft.Channel.ToLower() switch
                {
                    "email" => "Email",
                    "sms" => "SMS",
                    "push" => "Push",
                    _ => draft.Channel
                };
            }

            // Objective sanity check — reject suspiciously short extractions
            if (draft.Objective != null && draft.Objective.Trim().Length < 5)
                draft.Objective = null;

            // Name sanity check — reject single character garbage
            if (draft.Name != null && draft.Name.Trim().Length < 3)
                draft.Name = null;

            return draft;
        }

        // ════════════════════════════════════════════════════════════════════
        // MISSING FIELDS CHECK
        // ════════════════════════════════════════════════════════════════════

        private static List<string> GetMissingFields(CampaignDraftDto draft)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(draft.Channel)) missing.Add("channel");
            if (string.IsNullOrWhiteSpace(draft.Objective)) missing.Add("objective");
            if (string.IsNullOrWhiteSpace(draft.Name)) missing.Add("name");
            return missing;
        }

        // ════════════════════════════════════════════════════════════════════
        // NEXT QUESTION
        // Fast model — generates one short question, no complex reasoning
        // ════════════════════════════════════════════════════════════════════

        private async Task<(string Question, int Tokens, List<string> SuggestedNames)>
    GetNextQuestionAsync(CampaignDraftDto draft, List<string> missing)
        {
            try
            {
                var (json, tokens) = await CallFastAsync(
                    PromptTemplates.Campaign.NextQuestionSystem,
                    PromptTemplates.Campaign.NextQuestionUser(draft, missing),
                    maxTokens: 120); // slightly more for suggestions

                var parsed = JsonSerializer.Deserialize<JsonElement>(
                    CleanJson(json), _jsonOptions);

                var question = parsed.TryGetProperty("question", out var q)
                    ? q.GetString() ?? BuildFallbackQuestion(missing[0])
                    : BuildFallbackQuestion(missing[0]);

                // Parse optional name suggestions (only present when asking for name)
                var suggestions = new List<string>();
                if (parsed.TryGetProperty("suggestions", out var arr)
                    && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) suggestions.Add(s!);
                    }
                }

                return (question, tokens, suggestions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Next question generation failed — using fallback");
                return (BuildFallbackQuestion(missing[0]), 0, new());
            }
        }

        private static string BuildFallbackQuestion(string missingField) =>
            missingField switch
            {
                "channel" => "Will this be an Email campaign or SMS?",
                "objective" => "What is the main goal of this campaign?",
                "name" => "What would you like to name this campaign?",
                _ => "Could you provide more details?"
            };

        // ════════════════════════════════════════════════════════════════════
        // PERSIST CAMPAIGN
        // ════════════════════════════════════════════════════════════════════

        private async Task<Campaign> SaveCampaignAsync(CampaignDraftDto draft)
        {
            if (draft.Id.HasValue)
            {
                var existing = await _context.Campaigns.FindAsync(draft.Id.Value);
                if (existing != null)
                {
                    existing.Name = draft.Name ?? existing.Name;
                    existing.Objective = draft.Objective;
                    existing.Channel = draft.Channel ?? existing.Channel;
                    existing.SelectionId = draft.SelectionId;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return existing;
                }
            }

            var campaign = new Campaign
            {
                Name = draft.Name ?? "Untitled Campaign",
                Objective = draft.Objective,
                Channel = draft.Channel ?? "Email",
                SelectionId = draft.SelectionId,
                Status = "Draft",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Campaigns.Add(campaign);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Campaign saved: Id={Id}, Name={Name}, Channel={Channel}",
                campaign.Id, campaign.Name, campaign.Channel);

            return campaign;
        }

        // ════════════════════════════════════════════════════════════════════
        // CRUD OPERATIONS
        // ════════════════════════════════════════════════════════════════════

        public async Task<List<CampaignSummaryDto>> GetAllCampaignsAsync()
        {
            return await _context.Campaigns
                .Include(c => c.Selection)
                .Include(c => c.Content)
                .Include(c => c.Schedule)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new CampaignSummaryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Objective = c.Objective,
                    Channel = c.Channel,
                    Status = c.Status,
                    SelectionName = c.Selection != null ? c.Selection.Name : null,
                    SelectionId = c.SelectionId,
                    HasContent = c.Content != null,
                    HasSchedule = c.Schedule != null,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt
                })
                .ToListAsync();
        }

        public async Task<CampaignSummaryDto> GetCampaignByIdAsync(int id)
        {
            var campaign = await _context.Campaigns
                .Include(c => c.Selection)
                .Include(c => c.Content)
                .Include(c => c.Schedule)
                .FirstOrDefaultAsync(c => c.Id == id)
                ?? throw new KeyNotFoundException($"Campaign {id} not found");

            return new CampaignSummaryDto
            {
                Id = campaign.Id,
                Name = campaign.Name,
                Objective = campaign.Objective,
                Channel = campaign.Channel,
                Status = campaign.Status,
                SelectionName = campaign.Selection?.Name,
                HasContent = campaign.Content != null,
                HasSchedule = campaign.Schedule != null,
                CreatedAt = campaign.CreatedAt,
                UpdatedAt = campaign.UpdatedAt
            };
        }

        public async Task LinkSelectionAsync(int campaignId, int selectionId)
        {
            var campaign = await _context.Campaigns.FindAsync(campaignId)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found");

            var selection = await _context.Selections.FindAsync(selectionId)
                ?? throw new KeyNotFoundException($"Selection {selectionId} not found");

            campaign.SelectionId = selectionId;
            campaign.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Selection {SelId} linked to Campaign {CamId}",
                selectionId, campaignId);
        }

        public async Task DeleteCampaignAsync(int id)
        {
            var campaign = await _context.Campaigns.FindAsync(id)
                ?? throw new KeyNotFoundException($"Campaign {id} not found");

            _context.Campaigns.Remove(campaign);
            await _context.SaveChangesAsync();
        }

        // ════════════════════════════════════════════════════════════════════
        // STRATEGY GENERATION
        // C# computes segment profile → AI generates strategy + explanation
        // Power model — complex reasoning across engagement, value, channel
        // Same justification as selection building in AiService
        // ════════════════════════════════════════════════════════════════════

        public async Task<CampaignStrategyDto> GenerateStrategyAsync(
            int campaignId,
            GenerateStrategyRequestDto request)
        {
            _logger.LogInformation(
                "GenerateStrategyAsync — campaignId={Id}, channel={Ch}, objective={Obj}",
                campaignId, request.Channel, request.Objective);

            var catalog = await _aiService.BuildNodeCatalogPublicAsync();

            var profile = await _segmentProfileBuilder.BuildAsync(
                request.RootGroup,
                catalog,
                request.SelectionDescription);

            _logger.LogInformation(
                "Profile built: engagement={Eng}, value={Val}, summary={Summary}",
                profile.EngagementLevel, profile.ValueTier,
                profile.BehaviourSummary.Length > 80
                    ? profile.BehaviourSummary[..80] + "..."
                    : profile.BehaviourSummary);

            var systemPrompt =
    """
    You are a CRM campaign strategist. You receive a computed audience profile
    and generate a campaign strategy with full reasoning.

    Campaign types: reactivation, retention, conversion, winback, upsell
    Tones: urgent, friendly, premium, promotional

    Rules:
    - LongTermLapsed or Lapsed audience → reactivation or winback
    - AtRisk audience → retention
    - Active + Low value → conversion
    - Active + High value → upsell
    - High value + Lapsed → urgent tone
    - AtRisk → friendly tone
    - High value + Active → premium tone
    - Conversion campaign → promotional tone

    Return ONLY valid JSON:
    {
      "campaignType": "...",
      "tone": "...",
      "recommendedSendTime": "Thursday 12:00",
      "decisionFlow": {
        "engagementSignal": "specific fact about engagement from profile",
        "engagementConclusion": "what this means for campaign type",
        "valueSignal": "specific fact about customer value from profile",
        "valueConclusion": "what this means for investment level",
        "channelSignal": "specific fact about channel coverage from profile",
        "channelConclusion": "which channel this recommends and why",
        "finalDecision": "one sentence summary of the strategy chosen"
      },
      "explanation": [
        {
          "signal": "specific data fact from the profile",
          "implication": "what this means for strategy",
          "decision": "the concrete choice this drives"
        }
      ]
    }
    Provide exactly 3 explanation points.
    Reference actual numbers from the profile in every signal field.
    """;

            var userPrompt =
                $"""
                AUDIENCE PROFILE:
                Summary: {profile.BehaviourSummary}
                Engagement level: {profile.EngagementLevel}
                Customer value tier: {profile.ValueTier}
                Dominant recency: {profile.DominantRecency}
                Dominant loyalty tier: {profile.DominantLoyaltyTier}
                Dominant spend tier: {profile.DominantSpendTier}
                Email coverage: {profile.EmailCoveragePercent}%
                SMS coverage: {profile.SmsCoveragePercent}%
                Best visit day: {profile.RecommendedSendDay ?? "unknown"}
                Best visit hour: {profile.RecommendedSendHour ?? "unknown"}

                CAMPAIGN INTENT:
                Channel: {request.Channel}
                Objective: {request.Objective}

                Generate the campaign strategy with full explanation.
                """;

            // Power model — strategy requires reasoning across multiple profile dimensions
            var (json, tokens) = await CallPowerAsync(systemPrompt, userPrompt, maxTokens: 600);

            _logger.LogInformation("Strategy AI response ({Tokens} tokens): {Json}",
                tokens, json.Length > 200 ? json[..200] + "..." : json);

            var strategy = ParseStrategyJson(json);
            strategy.SegmentProfile = profile;
            strategy.TokensUsed = tokens;

            return strategy;
        }

        private CampaignStrategyDto ParseStrategyJson(string raw)
        {
            try
            {
                var cleaned = CleanJson(raw);
                var parsed = JsonSerializer.Deserialize<JsonElement>(cleaned, _jsonOptions);

                var explanation = new List<StrategyExplanationPointDto>();
                if (parsed.TryGetProperty("explanation", out var expArray))
                    foreach (var item in expArray.EnumerateArray())
                        explanation.Add(new StrategyExplanationPointDto
                        {
                            Signal = GetString(parsed: item, prop: "signal"),
                            Implication = GetString(parsed: item, prop: "implication"),
                            Decision = GetString(parsed: item, prop: "decision")
                        });

                // Parse decision flow — transparent reasoning chain
                // Shows the supervisor exactly why the AI chose each strategy
                var decisionFlow = new DecisionFlowDto();
                if (parsed.TryGetProperty("decisionFlow", out var df))
                {
                    decisionFlow.EngagementSignal = GetString(df, "engagementSignal");
                    decisionFlow.EngagementConclusion = GetString(df, "engagementConclusion");
                    decisionFlow.ValueSignal = GetString(df, "valueSignal");
                    decisionFlow.ValueConclusion = GetString(df, "valueConclusion");
                    decisionFlow.ChannelSignal = GetString(df, "channelSignal");
                    decisionFlow.ChannelConclusion = GetString(df, "channelConclusion");
                    decisionFlow.FinalDecision = GetString(df, "finalDecision");
                }

                return new CampaignStrategyDto
                {
                    CampaignType = GetString(parsed, "campaignType", "reactivation"),
                    Tone = GetString(parsed, "tone", "friendly"),
                    RecommendedSendTime = GetStringOrNull(parsed, "recommendedSendTime"),
                    Explanation = explanation,
                    DecisionFlow = decisionFlow
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Strategy JSON parse failed — using defaults");
                return new CampaignStrategyDto
                {
                    CampaignType = "reactivation",
                    Tone = "friendly",
                    Explanation = [],
                    DecisionFlow = new DecisionFlowDto()
                };
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // CAMPAIGN EXECUTION
        // ════════════════════════════════════════════════════════════════════

        public async Task<CampaignExecutionResultDto> ExecuteCampaignAsync(int campaignId)
        {
            var campaign = await _context.Campaigns
                .Include(c => c.Selection)
                .Include(c => c.Content)
                .FirstOrDefaultAsync(c => c.Id == campaignId)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found");

            var groups = await _context.SelectionGroups
                .Include(g => g.Rules)
                .Include(g => g.ChildGroups)
                .Where(g => g.SelectionId == campaign.SelectionId &&
                            g.ParentGroupId == null)
                .FirstOrDefaultAsync();

            if (groups == null)
                throw new InvalidOperationException("No selection rules found");

            var rootGroup = MapGroup(groups);

            (string whereClause, string joinClause) =
                await _sqlBuilder.BuildQueryPartsAsync(rootGroup);

            // SQL safety validation before executing any dynamic SQL
            // Deterministic guard — same philosophy as SelectionSanitizer
            ValidateSqlParts(whereClause, joinClause);

            var sql = $"SELECT COUNT(*) FROM Customers c {joinClause} WHERE {whereClause}";
            var count = await _context.Database
                .SqlQueryRaw<int>(sql)
                .FirstOrDefaultAsync();

            campaign.Status = "Running";
            campaign.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await Task.Delay(500);

            campaign.Status = "Completed";
            campaign.TotalReach = count;
            campaign.Delivered = count;
            campaign.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return new CampaignExecutionResultDto
            {
                CampaignId = campaignId,
                TotalReach = count,
                Delivered = count,
                Status = "Completed"
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // CAMPAIGN SIMULATION
        // C# computes known facts → Fast model interprets into prediction
        // Same pattern as confidence scoring: C# computes, AI explains
        // ════════════════════════════════════════════════════════════════════

        public async Task<CampaignSimulationDto> SimulateCampaignAsync(int campaignId)
        {
            var campaign = await _context.Campaigns
                .Include(c => c.Content)
                .FirstOrDefaultAsync(c => c.Id == campaignId)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found");

            // C# computes known facts — zero AI tokens
            var channelCoverage = campaign.Channel?.ToLower() switch
            {
                "email" => 92,
                "sms" => 50,
                "push" => 44,
                _ => 70
            };

            var contentText = (campaign.Content?.Subject ?? "") +
                              " " + (campaign.Content?.HtmlBody ?? "");

            var hasIncentive = new[]
            {
                "offer", "discount", "save", "exclusive", "free",
                "%", "deal", "reward", "special", "gift"
            }.Any(w => contentText.Contains(w, StringComparison.OrdinalIgnoreCase));

            var audienceSize = campaign.TotalReach > 0 ? campaign.TotalReach : 1000;

            // Fast model — interpretation of facts, not complex reasoning
            var (json, _) = await CallFastAsync(
                """
                You are a campaign performance analyst for retail marketing.
                Predict campaign performance based on the given data.
                Industry averages: Email open 15%, click 2.8%, SMS open 35%, click 5%

                Return ONLY valid JSON:
                {
                  "expectedOpenRate": 0.0,
                  "expectedClickRate": 0.0,
                  "riskLevel": "low|medium|high",
                  "riskFactors": ["factor1"],
                  "optimisationTips": ["tip1"]
                }

                Rules:
                - riskLevel "high" if audience < 500 OR channelCoverage < 60
                - riskLevel "low" if audience > 2000 AND channelCoverage > 85 AND hasIncentive
                - Otherwise "medium"
                - Provide 1-3 riskFactors and 1-3 optimisationTips
                - Keep all strings under 80 characters
                """,
                $"""
                Campaign: {campaign.Name}
                Channel: {campaign.Channel}
                Objective: {campaign.Objective}
                Audience size: {audienceSize:N0}
                Channel coverage: {channelCoverage}%
                Has incentive in content: {hasIncentive}
                """,
                maxTokens: 250);

            try
            {
                var cleaned = CleanJson(json);
                var simulation = JsonSerializer.Deserialize<CampaignSimulationDto>(
                    cleaned, _jsonOptions);

                if (simulation != null)
                    simulation.ExpectedDelivered =
                        (int)(audienceSize * (channelCoverage / 100.0));

                return simulation ?? BuildFallbackSimulation(audienceSize, channelCoverage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Simulation parse failed — using fallback");
                return BuildFallbackSimulation(audienceSize, channelCoverage);
            }
        }

        private static CampaignSimulationDto BuildFallbackSimulation(
            int audienceSize, int channelCoverage)
        {
            return new CampaignSimulationDto
            {
                ExpectedDelivered = (int)(audienceSize * (channelCoverage / 100.0)),
                ExpectedOpenRate = 15,
                ExpectedClickRate = 2.8f,
                RiskLevel = "medium",
                RiskFactors = new List<string>
                {
                    "No historical data available for this audience"
                },
                OptimisationTips = new List<string>
                {
                    "Consider adding a specific offer to improve open rate",
                    "Schedule for Thursday lunchtime for best engagement"
                }
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // SQL SAFETY VALIDATION
        // Deterministic C# guard before every raw SQL execution
        // Same philosophy as SelectionSanitizer — deterministic not probabilistic
        // ════════════════════════════════════════════════════════════════════

        private static readonly string[] ForbiddenSqlPatterns =
        {
            "--", ";", "DROP", "DELETE", "INSERT", "UPDATE",
            "EXEC", "EXECUTE", "xp_", "sp_", "UNION", "TRUNCATE",
            "ALTER", "CREATE", "GRANT", "REVOKE"
        };

        private void ValidateSqlParts(string whereClause, string joinClause)
        {
            var combined = (whereClause + " " + joinClause).ToUpper();

            foreach (var pattern in ForbiddenSqlPatterns)
            {
                if (combined.Contains(pattern.ToUpper()))
                {
                    _logger.LogError(
                        "SQL injection pattern detected: '{Pattern}'", pattern);
                    throw new InvalidOperationException(
                        $"SQL validation failed — forbidden pattern: {pattern}");
                }
            }

            var trimmedWhere = whereClause.Trim();
            if (!trimmedWhere.StartsWith("c.", StringComparison.OrdinalIgnoreCase) &&
                !trimmedWhere.StartsWith("(", StringComparison.OrdinalIgnoreCase) &&
                !trimmedWhere.StartsWith("EXISTS", StringComparison.OrdinalIgnoreCase) &&
                !trimmedWhere.StartsWith("NOT", StringComparison.OrdinalIgnoreCase) &&
                !trimmedWhere.StartsWith("a.", StringComparison.OrdinalIgnoreCase) &&
                !trimmedWhere.StartsWith("t.", StringComparison.OrdinalIgnoreCase) &&
                !trimmedWhere.StartsWith("v.", StringComparison.OrdinalIgnoreCase) &&
                trimmedWhere != "1=1")
            {
                _logger.LogError(
                    "Unexpected WHERE clause: '{Clause}'",
                    trimmedWhere.Length > 50
                        ? trimmedWhere[..50] + "..."
                        : trimmedWhere);
                throw new InvalidOperationException(
                    "SQL validation failed — unexpected WHERE clause format");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // MODEL ROUTING HELPERS
        // Reads model names from appsettings via _aiService properties
        // No hardcoded model strings anywhere in this class
        // ════════════════════════════════════════════════════════════════════

        // Fast model — extraction, question generation, simulation interpretation
        // Short structured output — quality difference vs power model negligible
        // Cost: ~10x cheaper per token than power model
        private Task<(string Response, int Tokens)> CallFastAsync(
            string system, string user, int maxTokens = 150)
        {
            return _aiService.CallPublicAsync(
                system, user, maxTokens,
                model: _aiService.FastModel);
        }

        // Power model — strategy generation
        // Complex reasoning: engagement + value + channel → campaign type + tone
        // Same justification as selection building in AiService
        private Task<(string Response, int Tokens)> CallPowerAsync(
            string system, string user, int maxTokens = 600)
        {
            return _aiService.CallPublicAsync(
                system, user, maxTokens,
                model: _aiService.PowerModel);
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════

        private static SelectionGroupDto MapGroup(SelectionGroup group)
        {
            return new SelectionGroupDto
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
        }

        private static string GetString(
            JsonElement parsed, string prop, string fallback = "")
        {
            try
            {
                return parsed.TryGetProperty(prop, out var v) &&
                       v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? fallback
                    : fallback;
            }
            catch { return fallback; }
        }

        private static string? GetStringOrNull(JsonElement parsed, string prop)
        {
            try
            {
                return parsed.TryGetProperty(prop, out var v) &&
                       v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
            }
            catch { return null; }
        }

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
    }
}