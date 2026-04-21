using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.DTOs.Grok;
using crm_ai.Helpers;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace crm_ai.Services
{
    public class AiService : IAiService
    {
        private readonly HttpClient _httpClient;
        private readonly GrokAiOptions _options;
        private readonly AppDbContext _context;
        private readonly ILogger<AiService> _logger;
        private readonly IMemoryCache _cache;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
        };

        private static readonly JsonSerializerOptions _camelCaseOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ── CHANGED: NodeInfo now carries AiLabel + SemanticCategory ─────────
        private sealed class NodeInfo
        {
            public int Id { get; set; }
            public int? ParentId { get; set; }     
            public string? NodeName { get; set; }
            public string? NodeDesc { get; set; }
            public string? DataType { get; set; }
            public string? EntityName { get; set; }
            public string? FieldName { get; set; }
            public string? ParentName { get; set; }
            public string? AiLabel { get; set; }
            public string? SemanticCategory { get; set; }
        }

        public AiService(
            IHttpClientFactory httpClientFactory,
            IOptions<GrokAiOptions> options,
            AppDbContext context,
            ILogger<AiService> logger,
            IMemoryCache cache)
        {
            _httpClient = httpClientFactory.CreateClient("GrokClient");
            _options = options.Value;
            _context = context;
            _logger = logger;
            _cache = cache;
        }

        // ════════════════════════════════════════════════════════════════════
        // 1. GENERATE DESCRIPTION
        // ════════════════════════════════════════════════════════════════════

        public async Task<AiDescriptionResponseDto> GenerateSelectionDescriptionAsync(
            SelectionGroupDto rootGroup)
        {
            var cacheKey = BuildCacheKey("desc_", rootGroup);

            if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
            {
                _logger.LogInformation("AI description served from cache");
                return new AiDescriptionResponseDto { Description = cached, TokensUsed = 0, FromCache = true };
            }

            var enrichedGroup = await EnrichGroupAsync(rootGroup);
            var userPrompt = SelectionPromptBuilder.BuildUserPrompt(enrichedGroup);
            var (description, tokensUsed) = await CallGroqAsync(PromptTemplates.Description.System, userPrompt);

            _cache.Set(cacheKey, description, TimeSpan.FromHours(2));
            return new AiDescriptionResponseDto { Description = description, TokensUsed = tokensUsed, FromCache = false };
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. GENERATE SELECTION FROM PROMPT
        // ════════════════════════════════════════════════════════════════════

        public async Task<AiSelectionResponseDto> GenerateSelectionFromPromptAsync(
            string prompt, string? name = null)
        {
            _logger.LogInformation("Generating selection from prompt | Length={Length}", prompt.Length);

            try
            {
                var catalog = await BuildNodeCatalogAsync();
                var filteredCatalog = await FilterCatalogByPromptAiAsync(prompt, catalog);
                var userPrompt = BuildSelectionUserPrompt(prompt, filteredCatalog);

                var (rawJson, tokens1) = await CallGroqAsync(
                    PromptTemplates.Selection.System, userPrompt,
                    maxTokens: 1500, model: _options.PowerModel);

                _logger.LogDebug("Raw AI selection JSON:\n{Json}", rawJson);

                var (rootGroup, unmatchedTerms, parseSuccess) = ParseSelectionJson(rawJson, catalog);

                if (!parseSuccess)
                {
                    _logger.LogWarning("AI returned invalid JSON for selection generation");
                    return new AiSelectionResponseDto
                    {
                        Name = name ?? "Untitled Selection",
                        Description = string.Empty,
                        RootGroup = rootGroup,
                        Confidence = -1,
                        UnmatchedTerms = unmatchedTerms,
                        TokensUsed = tokens1
                    };
                }

                var enrichedGroup = await EnrichGroupAsync(rootGroup);
                var enrichedPrompt = SelectionPromptBuilder.BuildUserPrompt(enrichedGroup);

                var confidenceTask = ScoreConfidenceAsync(prompt, rootGroup, catalog);
                var descriptionTask = CallGroqAsync(PromptTemplates.Description.System, enrichedPrompt);
                await Task.WhenAll(confidenceTask, descriptionTask);

                var (confidence, tokens2) = await confidenceTask;
                var (description, tokens3) = await descriptionTask;

                string selectionName = name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(selectionName))
                {
                    var (generatedName, _) = await CallGroqAsync(
                        PromptTemplates.Description.NameSystem,
                        PromptTemplates.Description.NameUser(description),
                        maxTokens: 30);
                    selectionName = generatedName.Trim('"', '.', ' ');
                }

                return new AiSelectionResponseDto
                {
                    Name = selectionName,
                    Description = description,
                    RootGroup = rootGroup,
                    Confidence = confidence,
                    UnmatchedTerms = unmatchedTerms,
                    TokensUsed = tokens1 + tokens2 + tokens3
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in GenerateSelectionFromPromptAsync");
                return new AiSelectionResponseDto
                {
                    Name = name ?? "AI Generated Selection",
                    Description = "The AI service is temporarily unavailable. Please build your selection using the visual builder.",
                    RootGroup = EmptyGroup(),
                    Confidence = 0,
                    UnmatchedTerms = new List<string> { "Service unavailable" },
                    TokensUsed = 0
                };
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // NODE CATALOG — CHANGED: reads AiLabel + SemanticCategory
        // ════════════════════════════════════════════════════════════════════

        private async Task<Dictionary<int, NodeCatalogItem>> BuildNodeCatalogAsync()
        {
            const string cacheKey = "node_catalog_v5"; // ── CHANGED: bumped version

            if (_cache.TryGetValue(cacheKey, out Dictionary<int, NodeCatalogItem>? cached) && cached != null)
                return cached;

            // ── CHANGED: select AiLabel and SemanticCategory from DB ──────────
            var nodes = await (
                from child in _context.TreeNodes
                where child.IsSelectable == 1
                join parent in _context.TreeNodes on child.ParentId equals parent.Id into parentJoin
                from p in parentJoin.DefaultIfEmpty()
                select new NodeInfo
                {
                    Id = child.Id,
                    ParentId = child.ParentId,              
                    NodeName = child.NodeName,
                    NodeDesc = child.NodeDesc,
                    DataType = child.DataType,
                    EntityName = child.EntityName,
                    FieldName = child.FieldName,
                    ParentName = p.NodeName,
                    AiLabel = child.AiLabel,
                    SemanticCategory = child.SemanticCategory
                }
            ).ToListAsync();

            var catalog = nodes.ToDictionary(n => n.Id, n => new NodeCatalogItem
            {
                Id = n.Id,
                ParentId = n.ParentId ?? 0,             // ADD
                ParentName = n.ParentName ?? "",         // ADD
                NodeName = n.AiLabel ?? n.NodeName ?? "",
                NodeDesc = n.AiLabel ?? n.NodeDesc ?? n.NodeName ?? "",
                DataType = n.DataType ?? "",
                Category = n.SemanticCategory ?? n.ParentName ?? n.EntityName ?? "General",
                SearchText = $"{n.AiLabel} {n.NodeName} {n.NodeDesc} {n.SemanticCategory} {n.ParentName} {n.DataType}".ToLower()
            });

            _cache.Set(cacheKey, catalog, TimeSpan.FromHours(1));
            _logger.LogInformation("Built node catalog with {Count} selectable nodes", catalog.Count);
            return catalog;
        }

        // ════════════════════════════════════════════════════════════════════
        // CATALOG FILTERING — unchanged logic, benefits automatically from
        // better SemanticCategory values in the catalog
        // ════════════════════════════════════════════════════════════════════

        private async Task<Dictionary<int, NodeCatalogItem>> FilterCatalogByPromptAiAsync(
            string prompt, Dictionary<int, NodeCatalogItem> fullCatalog)
        {
            try
            {
                var availableCategories = fullCatalog.Values
                    .Select(n => n.Category)
                    .Where(c => !string.IsNullOrWhiteSpace(c) && c != "General")
                    .Distinct().OrderBy(c => c).ToList();

                if (availableCategories.Count == 0) return fullCatalog;

                var categoryList = string.Join(", ", availableCategories);
                var (responseJson, filterTokens) = await CallGroqAsync(
                    PromptTemplates.Catalog.System,
                    PromptTemplates.Catalog.User(categoryList, prompt),
                    maxTokens: 100, skipDelay: true);

                _logger.LogInformation("Catalog filter used {Tokens} tokens", filterTokens);

                var neededCategories = DeserializeJsonArray(responseJson);

                if (neededCategories.Count == 0)
                {
                    _logger.LogWarning("Catalog filter returned empty — using full catalog");
                    return fullCatalog;
                }

                var neededSet = neededCategories.Select(c => c.Trim().ToLower()).ToHashSet();
                _logger.LogInformation("AI detected categories: [{Categories}]", string.Join(", ", neededCategories));

                var filtered = fullCatalog.Values
                    .Where(n => neededSet.Contains(n.Category.ToLower()))
                    .ToDictionary(n => n.Id);

                if (filtered.Count < 5)
                {
                    _logger.LogWarning("Catalog filter too narrow ({Count} nodes) — falling back to full", filtered.Count);
                    return fullCatalog;
                }

                _logger.LogInformation("Catalog filtered: {Full} → {Filtered} nodes (~{Saved} tokens saved)",
                    fullCatalog.Count, filtered.Count, (fullCatalog.Count - filtered.Count) * 8);

                return filtered;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog filtering failed — using full catalog");
                return fullCatalog;
            }
        }

        private List<string> DeserializeJsonArray(string raw)
        {
            var s = raw.Trim();
            if (s.StartsWith("```json")) s = s[7..];
            else if (s.StartsWith("```")) s = s[3..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();

            int start = s.IndexOf('[');
            int end = s.LastIndexOf(']');

            if (start < 0 || end <= start)
            {
                _logger.LogWarning("DeserializeJsonArray: no array found in: {Raw}", s[..Math.Min(200, s.Length)]);
                return new List<string>();
            }

            s = s[start..(end + 1)];

            try
            {
                return JsonSerializer.Deserialize<List<string>>(s, _jsonOptions) ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeserializeJsonArray parse failed for: {Raw}", s);
                return new List<string>();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // CATALOG STRING BUILDER — CHANGED: compressed format + cached string
        // ════════════════════════════════════════════════════════════════════

        private string BuildSelectionUserPrompt(
            string userRequest, Dictionary<int, NodeCatalogItem> catalog)
        {
            const int MaxNodes = 120;
            var nodes = catalog.Values.ToList();

            if (nodes.Count > MaxNodes)
            {
                var categoryCount = nodes.Select(n => n.Category).Distinct().Count();
                nodes = nodes
                    .GroupBy(n => n.Category).OrderBy(g => g.Key)
                    .SelectMany(g => g.Take(Math.Max(1, (int)Math.Ceiling((double)MaxNodes / categoryCount))))
                    .Take(MaxNodes).ToList();
            }

            // ── CHANGED: cache the catalog string keyed by the node ID set ───
            // This avoids rebuilding the same string on every call when the
            // filtered set is the same (very common for repeated turn topics).
            var catalogCacheKey = "catalog_str_" +
                string.Join(",", nodes.Select(n => n.Id).OrderBy(x => x));

            if (!_cache.TryGetValue(catalogCacheKey, out string? catalogStr))
            {
                // ── CHANGED: compressed format saves ~35% tokens vs old format ─
                // Old: [Gender]: ID=13 "Male" | ID=14 "Female"
                // New: Gender:13=Male,14=Female
                var sb = new StringBuilder("FILTERS (ID=label):\n");
                foreach (var group in nodes.GroupBy(n => n.Category).OrderBy(g => g.Key))
                {
                    sb.Append(group.Key).Append(':');
                    sb.AppendLine(string.Join(",", group.Select(n => $"{n.Id}={n.NodeName}")));
                }
                catalogStr = sb.ToString();
                _cache.Set(catalogCacheKey, catalogStr, TimeSpan.FromHours(1));
            }

            return catalogStr + "\nREQUEST:\n" + userRequest + "\nReturn JSON only:";
        }
        // Inside AiService — static, loaded once

        private static readonly Dictionary<string, SemanticGroupMeta> DataTypeSemantics = new()
        {
            ["visitrecency"] = new("visit_recency", "How recently they visited", "single_choice", 1),
            ["visitcount"] = new("visit_frequency", "How often they visit (count)", "single_choice", 2),
            ["loyaltysegment"] = new("loyalty_tier", "Their loyalty tier", "single_choice", 3),
            ["daysago"] = new("days_ago", "Days since last action", "single_choice", 4),
            ["spendrange"] = new("spend_range", "How much they typically spend", "single_choice", 5),
            ["agerange"] = new("age_range", "Age group", "multi_choice", 6),
            ["cityregion"] = new("region", "UK region", "multi_choice", 7),
            ["string"] = new("specific_value", "Specific value", "multi_choice", 8),
            ["hourofday"] = new("time_of_day", "Time of day they visit", "multi_choice", 9),
            ["dayofweek"] = new("day_of_week", "Day of week they visit", "multi_choice", 10),
            ["durationminutes"] = new("dwell_time", "How long they stay", "single_choice", 11),
            ["boolean"] = new("flag", "Yes/No flag", "single_choice", 12),
            ["notnull"] = new("contact_channel", "Contact channel availability", "multi_choice", 13),
            ["siteid"] = new("location_type", "Which site or location", "single_choice", 14),
            ["count"] = new("engagement_count", "How many times they engaged", "single_choice", 15),
            ["countdistinct"] = new("location_count", "Number of distinct locations", "single_choice", 16),
        };

        private sealed record SemanticGroupMeta(
            string GroupId,
            string FriendlyName,
            string DefaultType,   // single_choice or multi_choice
            int Priority);        // lower = shown first when multiple groups match

        /// <summary>
        /// Scans the filtered catalog to find which semantic groups are relevant to the prompt.
        /// Returns structured clarification blocks, or null if the prompt maps cleanly to one group.
        /// </summary>
        /// <summary>
        /// Detects ambiguity by scoring parent groups against the user's prompt words.
        /// Pure deterministic string matching — no AI call, no catalog filter.
        /// Returns structured clarification blocks when 2–4 parent groups score > 0.
        /// Returns null when the prompt maps clearly to 0 or 1 group (let normal build run).
        /// </summary>
        private async Task<List<ClarificationBlockDto>?> BuildClarificationPayloadAsync(
    string userMessage,
    Dictionary<int, NodeCatalogItem> catalog)
        {
            var words = userMessage
                .ToLower()
                .Split(new[] { ' ', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2)   // skip "in", "a", "to", "of", "is"
                .Distinct()
                .ToArray();

            if (words.Length == 0) return null;

            var scoredParents = catalog.Values
                .Where(n => n.ParentId > 0)
                .GroupBy(n => n.ParentId)
                .Select(g =>
                {
                    var semanticCategory = g.First().Category;

                    // Score 1: direct word match in node SearchText.
                    // Catches specific values: city names, ages, exact terms.
                    var searchScore = g.Sum(n =>
                        words.Count(w => n.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase)));

                    // Score 2: natural language → SemanticCategory keyword match.
                    // Catches phrases like "come often", "high spenders", "weekend visitors".
                    var categoryScore = SemanticCategoryKeywords.TryGetValue(semanticCategory, out var keywords)
                        ? words.Count(w => keywords.Any(k =>
                            string.Equals(k, w, StringComparison.OrdinalIgnoreCase) ||
                            k.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                            w.Contains(k, StringComparison.OrdinalIgnoreCase)))
                        : 0;

                    return new
                    {
                        ParentId = g.Key,
                        ParentName = g.First().ParentName,
                        DataType = g.First().DataType,
                        Nodes = g.OrderBy(n => n.Id).ToList(),
                        Score = searchScore + categoryScore
                    };
                })
                .Where(g => g.Score > 0)
                .OrderByDescending(g => g.Score)
                .Take(4)
                .ToList();

            _logger.LogInformation(
                "Clarification scoring for \"{Msg}\" | {Count} groups | Top: [{Top}]",
                userMessage,
                scoredParents.Count,
                string.Join(", ", scoredParents.Take(4).Select(g => $"{g.ParentName}={g.Score}")));

            if (scoredParents.Count <= 1) return null;

            var blocks = new List<ClarificationBlockDto>();

            foreach (var group in scoredParents.Take(3))
            {
                var options = group.Nodes
                    .Take(6)
                    .Select((n, i) => new ClarificationOptionDto
                    {
                        OptionId = $"opt_{i}",
                        Label = BuildOptionLabel(group.ParentName, n.NodeName),
                        Rules = new List<ClarificationRuleDto>
                        {
                    new() { TreeNodeId = n.Id, Operator = "=", Value = "" }
                        },
                        IsFallback = false
                    })
                    .ToList();

                options.Add(new ClarificationOptionDto
                {
                    OptionId = "opt_none",
                    Label = "None of these",
                    Rules = new(),
                    IsFallback = true
                });

                blocks.Add(new ClarificationBlockDto
                {
                    Id = $"block_{group.ParentId}",
                    Type = GetInteractionType(group.DataType),
                    Label = BuildBlockLabel(group.ParentName),
                    Options = options
                });
            }

            blocks = await RewriteClarificationLabelsAsync(userMessage, blocks);
            return blocks;
        }

        /// <summary>
        /// Takes the raw node labels and rewrites them into friendly question + option text.
        /// This is the ONLY AI call in the clarification flow.
        /// Input: raw blocks with NodeNames as labels
        /// Output: same structure with better human-readable text
        /// </summary>
        private async Task<List<ClarificationBlockDto>> RewriteClarificationLabelsAsync(
            string userPrompt,
            List<ClarificationBlockDto> blocks)
        {
            const string system =
                """
        You rewrite CRM filter option labels into friendly, plain-English UI text.
        The user typed a prompt. You are shown groups of filter options the system found.
        
        For each block:
        - Rewrite "label" as a short, clear question (max 8 words)
        - Rewrite each option's "label" as a natural phrase a non-technical user would understand
        - Keep "nodeIds" and "isFallback" exactly as-is
        - Keep "id" and "type" exactly as-is
        - Never add or remove options
        - Return ONLY valid JSON — the same array structure, rewritten
        
        Examples of good rewrites:
          "visitrecency" block label → "How recently did they visit?"
          "<= 7 days" option → "Within the last week"
          "15-31 days" option → "2–4 weeks ago"
          "loyaltysegment" block label → "Or pick a loyalty tier instead?"
          "Loyal" option → "Loyal customers"
          "Frequent" option → "Frequent visitors"
        """;

            try
            {
                var blocksJson = JsonSerializer.Serialize(blocks, _camelCaseOptions);
                var userPromptText = $"User typed: \"{userPrompt}\"\n\nBlocks to rewrite:\n{blocksJson}";

                var (responseJson, _) = await CallGroqAsync(
                    system, userPromptText,
                    maxTokens: 400, skipDelay: true);

                var cleaned = CleanJson(responseJson);

                // The response might be an array, not an object
                var rewritten = JsonSerializer.Deserialize<List<ClarificationBlockDto>>(
                    cleaned, _jsonOptions);

                if (rewritten != null && rewritten.Count == blocks.Count)
                    return rewritten;

                _logger.LogWarning("Label rewrite returned wrong count — using raw labels");
                return blocks;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Label rewrite failed — using raw labels");
                return blocks; // raw labels are still usable
            }
        }
        // ════════════════════════════════════════════════════════════════════
        // JSON PARSING — CHANGED: handles bare group response + wrapped response
        // ════════════════════════════════════════════════════════════════════

        private (SelectionGroupDto Group, List<string> UnmatchedTerms, bool Success)
            ParseSelectionJson(string rawJson, Dictionary<int, NodeCatalogItem> catalog)
        {
            try
            {
                var cleaned = CleanJson(rawJson);
                var parsed = JsonSerializer.Deserialize<JsonObject>(cleaned, _jsonOptions);
                if (parsed == null) return (EmptyGroup(), new(), false);

                // ── CHANGED: try rootGroup wrapper first, then bare group object ─
                SelectionGroupDto? rootGroup = null;

                var rootGroupNode = parsed["rootGroup"] ?? parsed["RootGroup"];
                if (rootGroupNode != null)
                {
                    rootGroup = JsonSerializer.Deserialize<SelectionGroupDto>(
                        rootGroupNode.ToJsonString(), _jsonOptions);
                }

                // If no wrapper found, check if the object itself IS a group
                // (this is the shape BuildNewGroupAsync returns and full-tree refine
                //  sometimes returns without the wrapper)
                if (rootGroup == null &&
                    (parsed.ContainsKey("logicalOperator") || parsed.ContainsKey("rules")))
                {
                    _logger.LogInformation("ParseSelectionJson: no rootGroup wrapper found — parsing as bare group");
                    rootGroup = JsonSerializer.Deserialize<SelectionGroupDto>(cleaned, _jsonOptions);
                }

                if (rootGroup == null) return (EmptyGroup(), new(), false);

                var unmatchedTerms = new List<string>();
                ValidateNodeIds(rootGroup, catalog, unmatchedTerms);
                return (rootGroup, unmatchedTerms, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse AI selection JSON: {Json}",
                    rawJson[..Math.Min(300, rawJson.Length)]);
                return (EmptyGroup(), new() { "JSON parse error" }, false);
            }
        }

        private void ValidateNodeIds(SelectionGroupDto group, Dictionary<int, NodeCatalogItem> catalog, List<string> unmatched)
        {
            if (group.Rules != null)
                foreach (var rule in group.Rules)
                {
                    if (!catalog.ContainsKey(rule.TreeNodeId))
                    {
                        unmatched.Add($"Unknown node ID: {rule.TreeNodeId}");
                        _logger.LogWarning("AI used invalid TreeNodeId: {Id}", rule.TreeNodeId);
                    }
                    rule.Operator ??= "=";
                    rule.Value ??= "";
                }

            if (group.Groups != null)
                foreach (var child in group.Groups)
                    ValidateNodeIds(child, catalog, unmatched);
        }

        private static string DescribeGroup(SelectionGroupDto group, Dictionary<int, NodeCatalogItem> catalog, int depth = 0)
        {
            var sb = new StringBuilder();
            var pad = new string(' ', depth * 2);
            sb.AppendLine($"{pad}[{group.LogicalOperator}]");

            if (group.Rules != null)
                foreach (var rule in group.Rules)
                {
                    var name = catalog.TryGetValue(rule.TreeNodeId, out var node)
                        ? $"{node.Category}: {node.NodeName}" : $"Unknown({rule.TreeNodeId})";
                    sb.AppendLine($"{pad}  • {name}");
                }

            if (group.Groups != null)
                foreach (var child in group.Groups)
                    sb.Append(DescribeGroup(child, catalog, depth + 1));

            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. VALIDATE SELECTION — unchanged
        // ════════════════════════════════════════════════════════════════════

        public async Task<AiValidationResponseDto> ValidateSelectionAsync(SelectionGroupDto rootGroup)
        {
            _logger.LogInformation("Validating selection rules with AI");
            var catalog = await BuildNodeCatalogAsync();

            if (CountRules(rootGroup) == 0)
                return new AiValidationResponseDto
                {
                    Summary = "Your selection has no rules.",
                    Status = "error",
                    Issues = new List<ValidationIssue>
                    {
                        new() { Severity = "error", Title = "Empty selection",
                            Detail = "You have not added any rules. Add at least one filter before saving." }
                    },
                    TokensUsed = 0
                };

            var readableRules = DescribeGroup(rootGroup, catalog);
            var (responseJson, tokensUsed) = await CallGroqAsync(
                PromptTemplates.Validation.System,
                PromptTemplates.Validation.ValidationUser(readableRules),
                maxTokens: 1000);

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonObject>(CleanJson(responseJson), _jsonOptions)
                    ?? throw new Exception("Null response from AI");

                var issues = new List<ValidationIssue>();
                var issuesArray = parsed["issues"]?.AsArray();
                if (issuesArray != null)
                    foreach (var item in issuesArray)
                    {
                        if (item == null) continue;
                        issues.Add(new ValidationIssue
                        {
                            Severity = item["severity"]?.GetValue<string>() ?? "warning",
                            Title = item["title"]?.GetValue<string>() ?? "Issue found",
                            Detail = item["detail"]?.GetValue<string>() ?? ""
                        });
                    }

                return new AiValidationResponseDto
                {
                    Summary = parsed["summary"]?.GetValue<string>() ?? "Could not summarise selection.",
                    Status = parsed["status"]?.GetValue<string>() ?? "valid",
                    Issues = issues,
                    TokensUsed = tokensUsed
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse validation response: {Json}", responseJson);
                return new AiValidationResponseDto
                {
                    Summary = "Could not complete validation. Please try again.",
                    Status = "warning",
                    Issues = new List<ValidationIssue>
                    {
                        new() { Severity = "warning", Title = "Validation unavailable",
                            Detail = "AI validation could not be completed at this time." }
                    },
                    TokensUsed = tokensUsed
                };
            }
        }

        private static int CountRules(SelectionGroupDto group)
        {
            var count = group.Rules?.Count ?? 0;
            if (group.Groups != null)
                foreach (var child in group.Groups)
                    count += CountRules(child);
            return count;
        }

        // ════════════════════════════════════════════════════════════════════
        // 4. CONVERSATIONAL FLOW — unchanged orchestration
        // ════════════════════════════════════════════════════════════════════

        public async Task<ConversationResponseDto> ContinueConversationAsync(ConversationRequestDto request)
        {
            _logger.LogInformation(
                "Conversation turn — {Count} messages, hasExistingRules={HasRules}, IntentConfirmed={IC}, Confirmed={C}",
                request.Messages.Count, request.CurrentRootGroup != null,
                request.IntentConfirmed, request.Confirmed);

            try
            {
                if (request.Messages == null || !request.Messages.Any())
                    return ErrorResponse("No messages provided.");

                var catalog = await BuildNodeCatalogAsync();

                var existingRulesContext = request.CurrentRootGroup != null
                    ? $"\n\nEXISTING RULES THE USER HAS BUILT:\n" +
                      $"{DescribeGroup(request.CurrentRootGroup, catalog)}\n" +
                      "When the user says 'also', 'add', 'remove', 'change', 'exclude', " +
                      "'instead', 'make it wider', 'actually' — REFINE these existing rules accordingly.\n"
                    : string.Empty;

                if (request.IntentConfirmed == true)
                {
                    bool isFirstBuild = request.CurrentRootGroup == null || CountRules(request.CurrentRootGroup) == 0;
                    _logger.LogInformation("IntentConfirmed → build (isFirstBuild={IsFirst})", isFirstBuild);
                    return await HandleBuildOrRefineAsync(
                        request.Messages, request.CurrentRootGroup,
                        catalog, existingRulesContext, request.Name, 0);
                }

                if (request.Confirmed == true)
                {
                    if (request.CurrentRootGroup == null)
                    {
                        _logger.LogWarning("Confirmed=true but CurrentRootGroup is null");
                        return ErrorResponse("Cannot confirm: no selection rules provided. Please try again.");
                    }

                    _logger.LogInformation("Confirmed → completed");
                    var enrichedForConfirm = await EnrichGroupAsync(request.CurrentRootGroup);
                    var (confirmedDesc, descTokens) = await CallGroqAsync(
                        PromptTemplates.Description.System,
                        SelectionPromptBuilder.BuildUserPrompt(enrichedForConfirm));

                    return new ConversationResponseDto
                    {
                        Status = "completed",
                        Message = "Selection confirmed! Review the rules and save when ready.",
                        Questions = new(),
                        Selection = new AiSelectionResponseDto
                        {
                            Name = request.Name ?? "AI Generated Selection",
                            Description = confirmedDesc,
                            RootGroup = request.CurrentRootGroup,
                            Confidence = 100,
                            UnmatchedTerms = new(),
                            TokensUsed = descTokens
                        },
                        TokensUsed = descTokens
                    };
                }

                bool hasRules = request.CurrentRootGroup != null && CountRules(request.CurrentRootGroup) > 0;
                if (hasRules)
                {
                    _logger.LogInformation("Refine turn — showing intent confirmation");
                    var currentRulesDesc = DescribeGroup(request.CurrentRootGroup!, catalog);
                    return await HandleIntentConfirmationAsync(
                        request.Messages, existingRulesContext, 0,
                        isRefine: true, currentRulesDesc: currentRulesDesc);
                }
                // ── HANDLE STRUCTURED CLARIFICATION ANSWERS ───────────────────────────
                if (!string.IsNullOrWhiteSpace(request.ClarificationStateId) &&
    request.ClarificationAnswers != null &&
    request.ClarificationAnswers.Count > 0)
                {
                    var state = LoadClarificationState(request.ClarificationStateId);
                    if (state == null)
                    {
                        _logger.LogWarning("Clarification state {Id} expired", request.ClarificationStateId);
                        return await HandleAskAsync(request.Messages, existingRulesContext, 0);
                    }

                    var resolvedAnswers = new List<(string BlockId, List<ClarificationRuleDto> Rules)>();

                    foreach (var answer in request.ClarificationAnswers)
                    {
                        var block = state.Blocks.FirstOrDefault(b => b.Id == answer.BlockId);
                        if (block == null)
                        {
                            _logger.LogWarning("BlockId {Id} not found in state {StateId} — skipping",
                                answer.BlockId, request.ClarificationStateId);
                            continue;
                        }

                        var rules = answer.SelectedOptionIds
                            .Select(optId => block.Options.FirstOrDefault(o => o.OptionId == optId))
                            .Where(opt => opt != null && !opt.IsFallback)
                            .SelectMany(opt => opt!.Rules)
                            .ToList();

                        if (rules.Count > 0)
                            resolvedAnswers.Add((answer.BlockId, rules));
                    }

                    if (resolvedAnswers.Count == 0)
                    {
                        _logger.LogInformation("All fallbacks selected — switching to free-text clarify");
                        return await HandleAskAsync(request.Messages, existingRulesContext, 0);
                    }

                    var clarificationAnswerDtos = resolvedAnswers
                        .Select(a => new ResolvedClarificationAnswer
                        {
                            BlockId = a.BlockId,
                            ResolvedRules = a.Rules
                        })
                        .ToList();

                    var builtGroup = BuildGroupFromResolvedAnswers(clarificationAnswerDtos);
                    NormalizeGroup(builtGroup);

                    var mergedRoot = request.CurrentRootGroup != null && CountRules(request.CurrentRootGroup) > 0
                        ? MergeGroupIntoRoot(request.CurrentRootGroup, builtGroup)
                        : builtGroup;

                    return await FinaliseSelectionResponseAsync(
                        mergedRoot, new List<string>(), request.Name,
                        previousTokens: 0,
                        isRefine: request.CurrentRootGroup != null && CountRules(request.CurrentRootGroup) > 0);
                }
                var (action, tokens1) = await DecideActionAsync(request.Messages);
                _logger.LogInformation("AI decided action: {Action}", action);

                if (action != "build")
                    return await HandleAskAsync(request.Messages, existingRulesContext, tokens1);

                return await HandleIntentConfirmationAsync(
                    request.Messages, existingRulesContext, tokens1,
                    isRefine: false, currentRulesDesc: string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in ContinueConversationAsync");
                return ErrorResponse("The AI service is temporarily unavailable. Please build your selection using the visual builder.");
            }
        }

        private async Task<(string Action, int Tokens)> DecideActionAsync(List<ConversationMessage> messages)
        {
            try
            {
                var lastUserMessage = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                var (response, tokens) = await CallGroqAsync(
                    PromptTemplates.Conversation.IntentSystem,
                    PromptTemplates.Conversation.IntentUser(lastUserMessage),
                    maxTokens: 10, skipDelay: true);

                var action = response.Trim().ToLower().Contains("build") ? "build" : "ask";
                _logger.LogInformation("Intent detection: \"{Message}\" → {Action} ({Tokens} tokens)",
                    lastUserMessage.Length > 50 ? lastUserMessage[..50] + "..." : lastUserMessage, action, tokens);

                return (action, tokens);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intent detection failed — defaulting to ask");
                return ("ask", 0);
            }
        }

        private async Task<ConversationResponseDto> HandleIntentConfirmationAsync(
            List<ConversationMessage> messages,
            string existingRulesContext,
            int previousTokens,
            bool isRefine = false,
            string currentRulesDesc = "")
        {
            var lastUserMessage = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
            var conversationText = string.Join("\n", messages.TakeLast(6).Select(m => $"{m.Role}: {m.Content}"));

            string systemPrompt, userPrompt;

            if (isRefine && !string.IsNullOrWhiteSpace(currentRulesDesc))
            {
                systemPrompt = PromptTemplates.Conversation.PreConfirmRefineSystem(currentRulesDesc);
                userPrompt = PromptTemplates.Conversation.PreConfirmUser($"user: {lastUserMessage}");
            }
            else
            {
                systemPrompt = PromptTemplates.Conversation.PreConfirmSystem(existingRulesContext);
                userPrompt = PromptTemplates.Conversation.PreConfirmUser(conversationText);
            }

            var (responseJson, tokens) = await CallGroqAsync(systemPrompt, userPrompt, maxTokens: 200);

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonObject>(CleanJson(responseJson), _jsonOptions);
                var summary = parsed?["summary"]?.GetValue<string>()
                    ?? (isRefine
                        ? $"I'll apply this change: {lastUserMessage}. Shall I apply this change?"
                        : $"I'll build a selection based on: {lastUserMessage}. Shall I build this selection?");

                return new ConversationResponseDto
                {
                    Status = "intent_confirmation",
                    Message = summary,
                    Questions = new(),
                    Selection = null,
                    TokensUsed = previousTokens + tokens
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intent confirmation parse failed — using fallback");
                return new ConversationResponseDto
                {
                    Status = "intent_confirmation",
                    Message = isRefine
                        ? $"I'll apply this change: {lastUserMessage}. Shall I apply this change?"
                        : $"I understood: {lastUserMessage}. Shall I build this selection?",
                    Questions = new(),
                    Selection = null,
                    TokensUsed = previousTokens + tokens
                };
            }
        }

        private async Task<ConversationResponseDto> HandleAskAsync(
            List<ConversationMessage> messages,
            string existingRulesContext,
            int previousTokens)
        {
            var lastUserMessage = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
            var catalog = await BuildNodeCatalogAsync();

            // ── TRY STRUCTURED CLARIFICATION FIRST ───────────────────────────
            var clarifications = await BuildClarificationPayloadAsync(lastUserMessage, catalog);

            if (clarifications != null && clarifications.Count > 0)
            {
                var stateId = SaveClarificationState(clarifications);   // ADD THIS LINE

                _logger.LogInformation(
                    "Structured clarification returned {Count} blocks for: \"{Msg}\"",
                    clarifications.Count, lastUserMessage);

                return new ConversationResponseDto
                {
                    Status = "clarifying_structured",
                    Message = $"I found a few possible meanings for '{lastUserMessage}'. Pick the ones that apply:",
                    Questions = new(),
                    Clarifications = clarifications,
                    ClarificationStateId = stateId,                     // ADD THIS LINE
                    Selection = null,
                    TokensUsed = previousTokens
                };
            }

            // ── FALLBACK: existing Groq free-text clarification ───────────────
            _logger.LogInformation("No structured clarification candidates — falling back to Groq ask");

            var conversationText = string.Join("\n",
                messages.TakeLast(6).Select(m => $"{m.Role}: {m.Content}"));

            var (responseJson, tokens) = await CallGroqAsync(
                PromptTemplates.Conversation.ClarifySystem(existingRulesContext),
                PromptTemplates.Conversation.ClarifyUser(conversationText),
                maxTokens: 300);

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonObject>(CleanJson(responseJson), _jsonOptions);
                var message = parsed?["message"]?.GetValue<string>()
                    ?? "I need a few more details:";
                var questions = parsed?["questions"]?.AsArray()
                    .Select(q => q?.GetValue<string>() ?? "")
                    .Where(q => !string.IsNullOrWhiteSpace(q))
                    .ToList() ?? new();

                return new ConversationResponseDto
                {
                    Status = "clarifying",
                    Message = message,
                    Questions = questions,
                    Clarifications = new(),
                    TokensUsed = previousTokens + tokens
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Groq clarify fallback parse failed");
                return new ConversationResponseDto
                {
                    Status = "clarifying",
                    Message = "Could you give me a bit more detail?",
                    Questions = new() { "What time frame are you thinking?", "Any location filter?" },
                    Clarifications = new(),
                    TokensUsed = previousTokens + tokens
                };
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // HANDLE BUILD OR REFINE
        // ════════════════════════════════════════════════════════════════════

        private async Task<ConversationResponseDto> HandleBuildOrRefineAsync(
            List<ConversationMessage> messages,
            SelectionGroupDto? currentRootGroup,
            Dictionary<int, NodeCatalogItem> catalog,
            string existingRulesContext,
            string? name,
            int previousTokens)
        {
            bool isRefine = currentRootGroup != null && CountRules(currentRootGroup) > 0;

            if (isRefine)
            {
                return await HandleRefineAsync(
                    messages, currentRootGroup!, catalog, name, previousTokens);
            }

            var conversationSummary = string.Join("\n", messages.TakeLast(8).Select(m => $"{m.Role}: {m.Content}"));
            var systemPrompt = PromptTemplates.Selection.System + PromptTemplates.Selection.BuildAdditional(existingRulesContext);

            var filteredCatalog = await FilterCatalogByPromptAiAsync(conversationSummary, catalog);
            var userPrompt = BuildSelectionUserPrompt(conversationSummary, filteredCatalog);

            var (rawJson, tokens1) = await CallGroqAsync(
                systemPrompt, userPrompt, maxTokens: 1500, model: _options.PowerModel);

            var (rootGroup, unmatchedTerms, parseSuccess) = ParseSelectionJson(rawJson, catalog);

            if (!parseSuccess)
            {
                _logger.LogWarning("Build failed to parse JSON");
                return new ConversationResponseDto
                {
                    Status = "error",
                    Message = "I couldn't build the selection. Could you try rephrasing?",
                    TokensUsed = previousTokens + tokens1
                };
            }

            return await FinaliseSelectionResponseAsync(
                rootGroup, unmatchedTerms, name, previousTokens + tokens1, isRefine: false);
        }

        // ════════════════════════════════════════════════════════════════════
        // REFINE
        // ════════════════════════════════════════════════════════════════════

        private async Task<ConversationResponseDto> HandleRefineAsync(
            List<ConversationMessage> messages,
            SelectionGroupDto currentRootGroup,
            Dictionary<int, NodeCatalogItem> catalog,
            string? name,
            int previousTokens)
        {
            var latestMessage = FindEffectiveUserRequest(messages);

            var (opType, classifyTokens) = await ClassifyRefineOperationAsync(latestMessage);
            _logger.LogInformation("Refine classified as: {OpType} ({Tokens} tokens)", opType, classifyTokens);

            if (opType == "add_group")
            {
                _logger.LogInformation("Refine add_group — delta-only path");

                var (newGroup, buildTokens) = await BuildNewGroupAsync(latestMessage, catalog);

                if (newGroup == null || CountRules(newGroup) == 0)
                {
                    _logger.LogWarning("add_group delta build returned empty — falling back to full refine");
                    return await HandleFullTreeRefineAsync(
                        messages, currentRootGroup, catalog, name,
                        previousTokens + classifyTokens + buildTokens);
                }

                var updatedRoot = DeepCloneGroup(currentRootGroup);
                updatedRoot.Groups ??= new List<SelectionGroupDto>();
                updatedRoot.Groups.Add(newGroup);

                var unmatchedTerms = new List<string>();
                ValidateNodeIds(updatedRoot, catalog, unmatchedTerms);

                return await FinaliseSelectionResponseAsync(
                    updatedRoot, unmatchedTerms, name,
                    previousTokens + classifyTokens + buildTokens, isRefine: true);
            }

            _logger.LogInformation("Refine {OpType} — full-tree path", opType);
            return await HandleFullTreeRefineAsync(
                messages, currentRootGroup, catalog, name,
                previousTokens + classifyTokens);
        }

        // ════════════════════════════════════════════════════════════════════
        // FULL TREE REFINE
        // ════════════════════════════════════════════════════════════════════

        private async Task<ConversationResponseDto> HandleFullTreeRefineAsync(
            List<ConversationMessage> messages,
            SelectionGroupDto currentRootGroup,
            Dictionary<int, NodeCatalogItem> catalog,
            string? name,
            int previousTokens)
        {
            var latestMessage = FindEffectiveUserRequest(messages);
            var currentRulesDesc = DescribeGroup(currentRootGroup, catalog);
            var currentJson = JsonSerializer.Serialize(currentRootGroup, _camelCaseOptions);

            var conversationSummary =
                "CURRENT RULES JSON — use this as your base, return a modified version. " +
                "Do NOT change anything not mentioned by the user:\n" +
                currentJson + "\n\n" +
                "CURRENT RULES (human-readable reference only):\n" +
                currentRulesDesc + "\n\n" +
                $"USER WANTS TO CHANGE: \"{latestMessage}\"\n\n" +
                "Apply ONLY this one change. Return the complete modified JSON:";

            _logger.LogInformation("Full-tree refine — JSON={Len} chars, delta=\"{Delta}\"",
                currentJson.Length, latestMessage);

            var filteredCatalog = await FilterCatalogByPromptAiAsync(latestMessage, catalog);
            var userPrompt = BuildSelectionUserPrompt(conversationSummary, filteredCatalog);

            var (rawJson, tokens1) = await CallGroqAsync(
                PromptTemplates.Selection.RefineSystem, userPrompt,
                maxTokens: 3000, model: _options.PowerModel);

            var (rootGroup, unmatchedTerms, parseSuccess) = ParseSelectionJson(rawJson, catalog);

            if (!parseSuccess)
            {
                _logger.LogWarning("Full-tree refine failed to parse JSON");
                // ── CHANGED: return the original tree unchanged rather than an error ─
                // This prevents a 3976-token call from resulting in lost user work.
                _logger.LogInformation("Full-tree refine: returning original tree unchanged");
                return new ConversationResponseDto
                {
                    Status = "error",
                    Message = "I couldn't apply that change — your current selection is unchanged. Could you try rephrasing?",
                    TokensUsed = previousTokens + tokens1,
                    // Return the original so the frontend can keep showing it
                    Selection = new AiSelectionResponseDto
                    {
                        Name = name ?? "Untitled Selection",
                        Description = string.Empty,
                        RootGroup = currentRootGroup,
                        Confidence = -1,
                        UnmatchedTerms = new(),
                        TokensUsed = 0
                    }
                };
            }

            return await FinaliseSelectionResponseAsync(
                rootGroup, unmatchedTerms, name, previousTokens + tokens1, isRefine: true);
        }

        // ════════════════════════════════════════════════════════════════════
        // CLASSIFY REFINE OPERATION
        // ════════════════════════════════════════════════════════════════════

        private async Task<(string OpType, int Tokens)> ClassifyRefineOperationAsync(string userMessage)
        {
            const string classifySystem =
                "You classify CRM rule edit operations. Return ONLY one word.\n\n" +
                "Return 'add_group' if the user wants to ADD a new group alongside existing ones.\n" +
                "Triggers: 'add another group', 'also a group', 'plus a group', 'or another group where', " +
                "'second group', 'third group', 'new group of', 'add a group of'.\n\n" +
                "Return 'modify_rule' if the user wants to CHANGE or REMOVE something inside an existing group.\n" +
                "Triggers: 'change', 'remove', 'delete', 'update', 'add [rule] to the group of', " +
                "'in the group of', 'exclude', 'also add [single rule]'.\n\n" +
                "Return 'restructure' if the user wants to rebuild the whole tree from scratch.\n\n" +
                "Return ONLY the single word: add_group, modify_rule, or restructure.";

            try
            {
                var (response, tokens) = await CallGroqAsync(
                    classifySystem,
                    $"Message: \"{userMessage}\"",
                    maxTokens: 5, skipDelay: true);

                var r = response.Trim().ToLower();
                var opType = r.Contains("add_group") ? "add_group"
                           : r.Contains("restructure") ? "restructure"
                           : "modify_rule";

                return (opType, tokens);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refine classification failed — defaulting to modify_rule");
                return ("modify_rule", 0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // BUILD NEW GROUP
        // ════════════════════════════════════════════════════════════════════

        private async Task<(SelectionGroupDto? Group, int Tokens)> BuildNewGroupAsync(
            string userMessage, Dictionary<int, NodeCatalogItem> catalog)
        {
            const string newGroupSystem =
                """
                You are a CRM rule group builder.
                Generate a SINGLE rule group JSON object for the described audience.
                Use ONLY the IDs provided in the catalog.
                logicalOperator must be "AND", "OR", or "EXCLUDE".
                operator is always "=" and value is always "".
                Return ONLY valid JSON — no markdown, no preamble, no rootGroup wrapper.

                CRITICAL TIME MAPPING:
                - "last week" / "within 7 days" = ID 5350
                - "last month" / "last 30 days" = ID 5352
                - "last 2 months" = ID 5353

                CRITICAL AGE MAPPING:
                - "18-24"=4, "25-34"=5, "35-44"=6, "45-54"=7, "55-64"=8, "65+"=9
                - Ranges → OR sub-group with each matching ID

                CRITICAL SPEND MAPPING:
                - "over £X" = OR sub-group of ALL spend-range IDs above X
                - "total visits over X" → find the visit-count node above X

                CRITICAL LOYALTY:
                - "loyal"=5503, "frequent"=5504, "lapsed"=5507, "long-term lapsed"=5508

                OUTPUT FORMAT — return ONLY this shape:
                {"logicalOperator":"AND","rules":[{"treeNodeId":123,"operator":"=","value":""}],"groups":[]}

                If multiple conditions, nest OR sub-groups inside the AND group as needed.
                NEVER wrap in a rootGroup. Return only the group object.
                """;

            try
            {
                var filteredCatalog = await FilterCatalogByPromptAiAsync(userMessage, catalog);
                var userPrompt = BuildSelectionUserPrompt($"Build a group for: \"{userMessage}\"", filteredCatalog);

                var (rawJson, tokens) = await CallGroqAsync(
                    newGroupSystem, userPrompt,
                    maxTokens: 800, model: _options.PowerModel);

                _logger.LogInformation("BuildNewGroupAsync raw response: {Json}", rawJson);

                var cleaned = CleanJson(rawJson);
                var parsed = JsonSerializer.Deserialize<JsonObject>(cleaned, _jsonOptions);

                // Unwrap rootGroup if the model added it anyway
                JsonNode? groupNode = parsed?["rootGroup"] ?? parsed?["RootGroup"];
                string groupJson = groupNode != null ? groupNode.ToJsonString() : cleaned;

                var group = JsonSerializer.Deserialize<SelectionGroupDto>(groupJson, _jsonOptions);
                if (group != null) NormalizeGroup(group);
                return (group, tokens);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BuildNewGroupAsync failed for message: {Msg}", userMessage);
                return (null, 0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // FINALISE
        // ════════════════════════════════════════════════════════════════════

        private async Task<ConversationResponseDto> FinaliseSelectionResponseAsync(
    SelectionGroupDto rootGroup,
    List<string> unmatchedTerms,
    string? name,
    int previousTokens,
    bool isRefine)
        {
            NormalizeGroup(rootGroup);

            // ── CHANGED: compute confidence deterministically ─────────────────
            var catalog = await BuildNodeCatalogAsync();
            var (confidence, confidenceReasons) = ComputeRuleBasedConfidence(rootGroup, catalog);

            _logger.LogInformation(
                "Confidence score: {Score}% | Reasons: {Reasons}",
                confidence, string.Join("; ", confidenceReasons));

            return new ConversationResponseDto
            {
                Status = "pending_confirmation",
                Message = isRefine
                    ? "I've updated your selection. Does this look correct?"
                    : "Here's what I've built. Does this look correct?",
                Questions = new(),
                Selection = new AiSelectionResponseDto
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Untitled Selection" : name,
                    Description = string.Empty,
                    RootGroup = rootGroup,
                    Confidence = confidence,                          // ← real score now
                    UnmatchedTerms = unmatchedTerms.Concat(confidenceReasons).ToList(),
                    TokensUsed = 0
                },
                TokensUsed = previousTokens
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // DEEP CLONE
        // ════════════════════════════════════════════════════════════════════

        private static SelectionGroupDto DeepCloneGroup(SelectionGroupDto group)
        {
            var json = JsonSerializer.Serialize(group, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            return JsonSerializer.Deserialize<SelectionGroupDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true
            })!;

        }


        // ════════════════════════════════════════════════════════════════════
        // 5. CHECK INTENT
        // ════════════════════════════════════════════════════════════════════

        public async Task<IntentCheckResponseDto> CheckIntentAsync(IntentCheckRequestDto request)
        {
            _logger.LogInformation("Checking intent: \"{Intent}\"", request.Intent);
            var catalog = await BuildNodeCatalogAsync();
            var readableRules = DescribeGroup(request.RootGroup, catalog);

            var (responseJson, tokens1) = await CallGroqAsync(
                PromptTemplates.IntentCheck.System,
                PromptTemplates.IntentCheck.User(request.Intent, readableRules),
                maxTokens: 800);

            IntentCheckResponseDto result;

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonObject>(CleanJson(responseJson), _jsonOptions);
                var gaps = new List<IntentGap>();
                var gapsArray = parsed?["gaps"]?.AsArray();
                if (gapsArray != null)
                    foreach (var item in gapsArray)
                    {
                        if (item == null) continue;
                        gaps.Add(new IntentGap
                        {
                            Type = item["type"]?.GetValue<string>() ?? "missing",
                            Description = item["description"]?.GetValue<string>() ?? ""
                        });
                    }

                result = new IntentCheckResponseDto
                {
                    Result = parsed?["result"]?.GetValue<string>() ?? "partial",
                    WhatItDoes = parsed?["whatItDoes"]?.GetValue<string>() ?? "",
                    WhatYouWanted = parsed?["whatYouWanted"]?.GetValue<string>() ?? request.Intent,
                    Gaps = gaps,
                    TokensUsed = tokens1
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse intent check response");
                return new IntentCheckResponseDto
                {
                    Result = "partial",
                    WhatItDoes = "Could not analyse rules.",
                    WhatYouWanted = request.Intent,
                    Gaps = new(),
                    TokensUsed = tokens1
                };
            }

            if (result.Result != "match")
            {
                try
                {
                    var fixUserPrompt = BuildSelectionUserPrompt(
                        $"Build a rule tree that exactly matches this intention: \"{request.Intent}\"", catalog);
                    var (fixJson, tokens2) = await CallGroqAsync(
                        PromptTemplates.Selection.System + PromptTemplates.Selection.FixIntentAdditional,
                        fixUserPrompt, maxTokens: 1500, model: _options.PowerModel);

                    var (fixedGroup, _, fixParseSuccess) = ParseSelectionJson(fixJson, catalog);
                    if (fixParseSuccess && CountRules(fixedGroup) > 0)
                    {
                        var enriched = await EnrichGroupAsync(fixedGroup);
                        var (fixDesc, tokens3) = await CallGroqAsync(
                            PromptTemplates.Description.System,
                            SelectionPromptBuilder.BuildUserPrompt(enriched));

                        result.SuggestedFix = fixedGroup;
                        result.SuggestedFixDescription = fixDesc;
                        result.TokensUsed += tokens2 + tokens3;
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not generate suggested fix"); }
            }

            return result;
        }

        // ════════════════════════════════════════════════════════════════════
        // GROQ API CALL
        // ════════════════════════════════════════════════════════════════════

        private async Task<(string Response, int TokensUsed)> CallGroqAsync(
            string systemPrompt,
            string userPrompt,
            int maxTokens = 300,
            bool skipDelay = false,
            string? model = null)
        {
            const int maxRetries = 3;
            var effectiveModel = model ?? _options.Model;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (!skipDelay)
                    await Task.Delay(500);

                var request = new GrokRequest
                {
                    Model = effectiveModel,
                    Temperature = 0.1f,
                    MaxTokens = maxTokens,
                    Messages = new List<GrokMessage>
                    {
                        new() { Role = "system", Content = systemPrompt },
                        new() { Role = "user",   Content = userPrompt   }
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

                HttpResponseMessage httpResponse;
                try
                {
                    httpResponse = await _httpClient.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
                }
                catch (Exception ex)
                {
                    if (attempt < maxRetries - 1)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
                        continue;
                    }
                    throw new InvalidOperationException("AI service is currently unavailable.", ex);
                }

                var statusCode = (int)httpResponse.StatusCode;

                if (statusCode == 429 && attempt < maxRetries - 1)
                {
                    _logger.LogWarning("Groq 429 on attempt {Attempt} — backing off", attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
                    continue;
                }

                if ((statusCode == 413 || statusCode == 404) && effectiveModel != _options.Model)
                {
                    var errorBody = await httpResponse.Content.ReadAsStringAsync();
                    _logger.LogWarning("HTTP {Status} on '{Model}' — falling back to '{Fast}'. Detail: {Body}",
                        statusCode, effectiveModel, _options.Model, errorBody);
                    return await CallGroqAsync(systemPrompt, userPrompt, maxTokens, skipDelay, _options.Model);
                }

                if (!httpResponse.IsSuccessStatusCode)
                {
                    var errorBody = await httpResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Groq API error {Status}: {Body}", httpResponse.StatusCode, errorBody);
                    throw new InvalidOperationException($"AI service returned {httpResponse.StatusCode}. Detail: {errorBody}");
                }

                var responseJson = await httpResponse.Content.ReadAsStringAsync();
                var grokResponse = JsonSerializer.Deserialize<GrokResponse>(responseJson);

                var firstChoice = grokResponse?.Choices?.FirstOrDefault();
                var responseText = firstChoice?.Message?.Content?.Trim() ?? "";
                var tokensUsed = grokResponse?.Usage?.TotalTokens ?? 0;

                if (firstChoice?.FinishReason == "length" && effectiveModel != _options.Model)
                {
                    _logger.LogWarning("finish_reason=length on '{Model}' — retrying with '{Fast}'", effectiveModel, _options.Model);
                    return await CallGroqAsync(systemPrompt, userPrompt, maxTokens, skipDelay, _options.Model);
                }

                _logger.LogInformation("Groq call complete ({Tokens} tokens, model={Model}, attempt={Attempt})",
                    tokensUsed, effectiveModel, attempt + 1);
                _logger.LogInformation("BILLING | Model={Model} | Tokens={Tokens} | MaxTokens={MaxTokens}",
                    effectiveModel, tokensUsed, maxTokens);

                return (responseText, tokensUsed);
            }

            throw new InvalidOperationException("AI service unavailable after retries.");
        }

        // ════════════════════════════════════════════════════════════════════
        // NODE ENRICHMENT
        // ════════════════════════════════════════════════════════════════════

        private async Task<EnrichedGroupDto> EnrichGroupAsync(SelectionGroupDto group)
        {
            var allIds = CollectNodeIds(group);
            var nodes = await (
                from child in _context.TreeNodes
                where allIds.Contains(child.Id)
                join parent in _context.TreeNodes on child.ParentId equals parent.Id into parentJoin
                from p in parentJoin.DefaultIfEmpty()
                select new NodeInfo
                {
                    Id = child.Id,
                    NodeName = child.NodeName,
                    NodeDesc = child.NodeDesc,
                    DataType = child.DataType,
                    EntityName = child.EntityName,
                    FieldName = child.FieldName,
                    ParentName = p.NodeName,
                    AiLabel = child.AiLabel,
                    SemanticCategory = child.SemanticCategory
                }
            ).ToDictionaryAsync(n => n.Id);

            return CloneEnriched(group, nodes);
        }

        private static EnrichedGroupDto CloneEnriched(SelectionGroupDto group, Dictionary<int, NodeInfo> nodeMap)
        {
            return new EnrichedGroupDto
            {
                LogicalOperator = group.LogicalOperator,
                Rules = group.Rules?.Select(r =>
                {
                    if (!nodeMap.TryGetValue(r.TreeNodeId, out var node))
                        return new EnrichedRuleDto
                        {
                            TreeNodeId = r.TreeNodeId,
                            NodeName = $"Filter {r.TreeNodeId}",
                            Category = "Unknown",
                            DataType = null,
                            Value = r.Value
                        };

                    // ── CHANGED: prefer AiLabel for the name shown in descriptions ─
                    return new EnrichedRuleDto
                    {
                        TreeNodeId = r.TreeNodeId,
                        NodeName = node.AiLabel
                                  ?? (!string.IsNullOrWhiteSpace(node.NodeDesc) ? node.NodeDesc! : node.NodeName)
                                  ?? $"Node {r.TreeNodeId}",
                        Category = node.SemanticCategory
                                  ?? (!string.IsNullOrWhiteSpace(node.ParentName) ? node.ParentName! : node.EntityName)
                                  ?? "Customer",
                        DataType = node.DataType,
                        Value = r.Value
                    };
                }).ToList(),
                Groups = group.Groups?.Select(g => CloneEnriched(g, nodeMap)).ToList()
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // CONFIDENCE SCORING
        // ════════════════════════════════════════════════════════════════════
        // ════════════════════════════════════════════════════════════════════
        // CONFIDENCE SCORING — Deterministic rule-based engine
        // ════════════════════════════════════════════════════════════════════

        private static (int Score, string[] Reasons) ComputeRuleBasedConfidence(
            SelectionGroupDto rootGroup,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            var reasons = new List<string>();
            int score = 0;

            int totalRules = CountRules(rootGroup);
            int totalGroups = CountGroups(rootGroup);

            // ── 1. RULE COUNT (0–25 pts) ─────────────────────────────────────
            // A good selection has 2–12 rules. Too few = too broad. Too many = too narrow.
            if (totalRules == 0)
            {
                reasons.Add("No rules defined");
                return (0, reasons.ToArray());
            }
            else if (totalRules == 1)
            {
                score += 5;
                reasons.Add("Only 1 rule — very broad");
            }
            else if (totalRules <= 12)
            {
                score += 25;
            }
            else
            {
                score += 15;
                reasons.Add("Many rules — selection may be too narrow");
            }

            // ── 2. CATEGORY DIVERSITY (0–25 pts) ─────────────────────────────
            // Good selections combine multiple data dimensions (Gender + City + Spend etc.)
            var usedCategories = CollectUsedCategories(rootGroup, catalog);
            var categoryCount = usedCategories.Count;

            if (categoryCount >= 3)
            {
                score += 25;
            }
            else if (categoryCount == 2)
            {
                score += 15;
                reasons.Add("Only 2 data dimensions used");
            }
            else
            {
                score += 5;
                reasons.Add("Only 1 data dimension — consider adding more filters");
            }

            // ── 3. LOGICAL STRUCTURE (0–20 pts) ──────────────────────────────
            // Selections with proper group structure are more precise
            if (totalGroups >= 2)
            {
                score += 20; // Has sub-groups — well structured
            }
            else if (totalGroups == 1)
            {
                score += 10;
            }

            // ── 4. HAS TARGETING DIMENSION (0–15 pts) ────────────────────────
            // Must have at least one of: location, demographic, behavioural
            bool hasLocation = usedCategories.Any(c =>
                c.Contains("city", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("location", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("region", StringComparison.OrdinalIgnoreCase));

            bool hasDemographic = usedCategories.Any(c =>
                c.Contains("gender", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("age", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("birth", StringComparison.OrdinalIgnoreCase));

            bool hasBehavioural = usedCategories.Any(c =>
                c.Contains("spend", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("visit", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("loyalty", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("purchase", StringComparison.OrdinalIgnoreCase));

            int dimensionCount = (hasLocation ? 1 : 0) + (hasDemographic ? 1 : 0) + (hasBehavioural ? 1 : 0);

            if (dimensionCount >= 2)
                score += 15;
            else if (dimensionCount == 1)
                score += 8;
            else
            {
                score += 0;
                reasons.Add("No clear targeting dimension (location/demographic/behavioural)");
            }

            // ── 5. NO ORPHAN GROUPS (0–15 pts) ───────────────────────────────
            // Groups with zero rules are wasted structure
            bool hasEmptyGroups = HasEmptyGroups(rootGroup);
            if (!hasEmptyGroups)
                score += 15;
            else
                reasons.Add("Some groups have no rules");

            // ── 6. PENALTY: EXCLUDE-ONLY ROOT ────────────────────────────────
            if (rootGroup.LogicalOperator == "EXCLUDE" && (rootGroup.Rules?.Count ?? 0) == 0)
            {
                score = Math.Max(0, score - 20);
                reasons.Add("Root group is EXCLUDE with no base — logically empty");
            }

            score = Math.Clamp(score, 0, 100);
            return (score, reasons.ToArray());
        }

        private static int CountGroups(SelectionGroupDto group)
        {
            int count = 1;
            if (group.Groups != null)
                foreach (var child in group.Groups)
                    count += CountGroups(child);
            return count;
        }

        private static HashSet<string> CollectUsedCategories(
            SelectionGroupDto group, Dictionary<int, NodeCatalogItem> catalog)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (group.Rules != null)
                foreach (var rule in group.Rules)
                    if (catalog.TryGetValue(rule.TreeNodeId, out var node))
                        cats.Add(node.Category);
            if (group.Groups != null)
                foreach (var child in group.Groups)
                    cats.UnionWith(CollectUsedCategories(child, catalog));
            return cats;
        }

        private static bool HasEmptyGroups(SelectionGroupDto group)
        {
            if ((group.Rules == null || group.Rules.Count == 0) &&
                (group.Groups == null || group.Groups.Count == 0))
                return true;
            if (group.Groups != null)
                foreach (var child in group.Groups)
                    if (HasEmptyGroups(child)) return true;
            return false;
        }

        private Task<(int Score, int Tokens)> ScoreConfidenceAsync(
        string originalPrompt, SelectionGroupDto rootGroup, Dictionary<int, NodeCatalogItem> catalog)
        {
            var (score, reasons) = ComputeRuleBasedConfidence(rootGroup, catalog);

            _logger.LogInformation(
                "Confidence score: {Score}% | Reasons: {Reasons}",
                score, string.Join("; ", reasons));

            return Task.FromResult((score, 0)); // 0 tokens — no Groq call
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════

        private static HashSet<int> CollectNodeIds(SelectionGroupDto group)
        {
            var ids = new HashSet<int>();
            if (group.Rules != null) foreach (var r in group.Rules) ids.Add(r.TreeNodeId);
            if (group.Groups != null) foreach (var child in group.Groups) ids.UnionWith(CollectNodeIds(child));
            return ids;
        }
        // ── Add this helper anywhere in AiService ──────────────────────────────
        private static string FindEffectiveUserRequest(List<ConversationMessage> messages)
        {
            // Confirmation phrases that should be skipped to find the real request
            var confirmationPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "yes build it", "yes, build it", "build it", "confirm",
        "ok", "okay", "do it", "go ahead", "yes please", "sure", "proceed",
        "apply it", "apply", "yes apply", "yes, apply"
    };

            // Walk backwards through user messages, skipping confirmations
            var userMessages = messages
                .Where(m => m.Role == "user")
                .ToList();

            for (int i = userMessages.Count - 1; i >= 0; i--)
            {
                var content = userMessages[i].Content?.Trim() ?? "";
                if (!confirmationPhrases.Contains(content))
                    return content;
            }

            // Fallback — shouldn't happen
            return messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        }
        // ════════════════════════════════════════════════════════════════════
        // CLARIFICATION STATE HELPERS
        // ════════════════════════════════════════════════════════════════════

        private const string ClarificationCachePrefix = "clarif_";
        private static readonly TimeSpan ClarificationTtl = TimeSpan.FromMinutes(10);

        private string SaveClarificationState(List<ClarificationBlockDto> blocks)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            var state = new ClarificationState { Id = id, Blocks = blocks };
            _cache.Set(ClarificationCachePrefix + id, state, ClarificationTtl);
            _logger.LogInformation("Clarification state saved: {Id}", id);
            return id;
        }

        private ClarificationState? LoadClarificationState(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            _cache.TryGetValue(ClarificationCachePrefix + id, out ClarificationState? state);
            return state;
        }
        private static readonly HashSet<string> MultiSelectDataTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "agerange", "cityregion", "string", "dayofweek", "hourofday", "notnull", "siteid"
            };
        // ════════════════════════════════════════════════════════════════════
        // SEMANTIC CATEGORY KEYWORDS — bridges natural language to categories
        // Only needs updating if you add a new SemanticCategory to the tree
        // ════════════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, string[]> SemanticCategoryKeywords =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Age"] = new[]
            {
        "age", "old", "young", "years", "born", "generation",
        "teenager", "teen", "millennial", "senior", "elderly",
        "adult", "youth", "older", "younger",
        "18", "25", "35", "45", "55", "65"
            },

                ["Gender"] = new[]
            {
        "gender", "male", "female", "men", "women", "man",
        "woman", "ladies", "gents", "boys", "girls", "sex"
            },

                ["Location"] = new[]
            {
        "location", "city", "region", "area", "where", "local",
        "london", "manchester", "birmingham", "edinburgh", "glasgow",
        "bristol", "leeds", "liverpool", "sheffield", "cardiff",
        "belfast", "scotland", "wales", "north", "south", "east",
        "west", "midlands", "uk", "england", "ireland", "nearby",
        "postcode", "town", "live", "living", "from", "based"
            },

                ["Recency"] = new[]
            {
        "recent", "recently", "last", "visit", "visited", "came",
        "come", "ago", "days", "week", "month", "yesterday",
        "today", "often", "back", "returned", "since", "within",
        "new", "haven"
            },

                ["Visits"] = new[]
            {
        "visit", "visits", "visited", "come", "came", "times",
        "often", "frequently", "count", "number", "many",
        "multiple", "once", "twice", "location", "stores",
        "sites", "places", "regular", "repeat"
            },

                ["Loyalty"] = new[]
            {
        "loyal", "loyalty", "frequent", "occasional", "infrequent",
        "lapsed", "inactive", "churned", "returning", "regular",
        "dormant", "engaged", "tier", "segment", "status",
        "vip", "often", "come", "never", "lost", "retention",
        "win", "back"
            },

                ["Spend"] = new[]
            {
        "spend", "spent", "spending", "value", "transaction",
        "purchase", "purchased", "bought", "buy", "money",
        "average", "total", "high", "low", "expensive", "cheap",
        "over", "under", "more", "less", "basket", "order",
        "revenue", "sales", "£", "pound", "pounds"
            },

                ["Dwell Time"] = new[]
            {
        "dwell", "stay", "time", "duration", "minutes", "hours",
        "long", "quick", "fast", "slow", "browse", "browsing",
        "store"
            },

                ["Visit Pattern"] = new[]
            {
        "day", "time", "when", "pattern", "typical", "usual",
        "morning", "afternoon", "evening", "night", "weekend",
        "weekday", "monday", "tuesday", "wednesday", "thursday",
        "friday", "saturday", "sunday", "midday", "lunch",
        "late", "early", "peak", "hour"
            },

                ["Email Engagement"] = new[]
            {
        "email", "emails", "open", "opened", "click", "clicked",
        "engaged", "engagement", "gmail", "hotmail", "yahoo",
        "outlook", "domain", "newsletter", "campaign",
        "read", "unsubscribed", "inbox", "mailing"
            },

                ["SMS Engagement"] = new[]
            {
        "sms", "text", "texted", "message", "messages", "mobile",
        "phone", "delivered", "undelivered", "received"
            },

                ["Contact"] = new[]
            {
        "contact", "contactable", "reachable", "emailable",
        "email", "phone", "sms", "mail", "post", "opted",
        "consent", "available", "mailable", "phoneable",
        "channel", "communicate", "reach"
            },

                ["Site"] = new[]
            {
        "site", "store", "shop", "branch", "location", "venue",
        "favourite", "preferred", "home", "usual", "interacted",
        "visited"
            },

                ["Profile"] = new[]
            {
        "survey", "gift", "card", "voucher", "name", "valid",
        "invalid", "profile", "participant", "holder", "consent"
            },
            };

        private static string GetInteractionType(string dataType) =>
            MultiSelectDataTypes.Contains(dataType) ? "multi_choice" : "single_choice";

        private static string BuildBlockLabel(string parentName) => parentName switch
        {
            "Recency" => "How recently should they have visited?",
            "Total visits in last 12 months" => "How many times should they have visited?",
            "Segment" => "Or are you thinking of a loyalty tier?",
            "Average transaction value" => "What average spend are you targeting?",
            "Total transaction value" => "What total spend are you targeting?",
            "Age" => "Which age group?",
            "Gender" => "Which gender?",
            "Regions" => "Which UK region?",
            "Total Dwell Time" => "How long should their total dwell time be?",
            "Ave Dwell Time" => "How long should their average dwell time be?",
            "Day of week" => "Which days of the week?",
            "Time of day" => "Which time of day?",
            "Open recency" => "How recently should they have opened an email?",
            "Click Recency" => "How recently should they have clicked?",
            "Locations" => "Which type of location?",
            _ => $"Which {parentName.ToLower()}?"
        };

        private static string BuildOptionLabel(string parentName, string nodeName) => parentName switch
        {
            "Recency" => nodeName switch
            {
                "Yesterday" => "Yesterday",
                "<= 7 days" => "Within the last 7 days",
                "8-14 days" => "8 to 14 days ago",
                "15-31 days" => "15 to 31 days ago",
                "1-2 months" => "1 to 2 months ago",
                "2-3 months" => "2 to 3 months ago",
                "3-4 Months" => "3 to 4 months ago",
                "4 months +" => "More than 4 months ago",
                _ => nodeName
            },
            "Segment" => nodeName switch
            {
                "Loyal" => "Loyal customers",
                "Frequent" => "Frequent visitors",
                "Occasional" => "Occasional visitors",
                "Infrequent" => "Infrequent visitors",
                "Lapsed" => "Lapsed customers",
                "Long-term lapsed" => "Long-term lapsed",
                "Never" => "Never visited",
                _ => nodeName
            },
            _ => nodeName
        };

        private static string BuildCacheKey(string prefix, object obj)
        {
            var json = JsonSerializer.Serialize(obj);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
            return $"{prefix}{Convert.ToHexString(hash)[..16]}";
        }

        private static SelectionGroupDto EmptyGroup() =>
            new() { LogicalOperator = "AND", Rules = new(), Groups = new() };

        private static ConversationResponseDto ErrorResponse(string message) =>
            new() { Status = "error", Message = message, TokensUsed = 0 };

        private static SelectionGroupDto NormalizeGroup(SelectionGroupDto group)
        {
            group.Rules ??= new List<SelectionRuleDto>();
            group.Groups ??= new List<SelectionGroupDto>();
            group.LogicalOperator ??= "AND";
            foreach (var rule in group.Rules)
            {
                rule.Operator ??= "=";
                rule.Value ??= "";
            }
            foreach (var child in group.Groups)
                NormalizeGroup(child);
            return group;
        }

        // ════════════════════════════════════════════════════════════════════
        // CleanJson — 4-strategy JSON repair
        // ════════════════════════════════════════════════════════════════════

        private string CleanJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            var s = raw.Trim();
            if (s.StartsWith("```json")) s = s[7..];
            else if (s.StartsWith("```")) s = s[3..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim().TrimEnd(',');

            if (s.TrimStart().StartsWith("[")) return s;

            if (TryParseJsonObject(s, out _)) return s;

            int start = s.IndexOf('{');
            int end = s.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var extracted = s[start..(end + 1)];
                if (TryParseJsonObject(extracted, out _)) return extracted;

                var repaired = RepairJson(extracted);
                if (TryParseJsonObject(repaired, out _))
                {
                    _logger.LogInformation("CleanJson strategy 3 (RepairJson) succeeded");
                    return repaired;
                }

                var longest = LongestBalancedJson(s);
                if (longest != null && TryParseJsonObject(longest, out _))
                {
                    _logger.LogInformation("CleanJson strategy 4 (LongestBalanced) succeeded");
                    return longest;
                }

                _logger.LogError("CleanJson: all strategies failed — returning best-effort repaired string");
                return repaired;
            }

            _logger.LogError("CleanJson: no {{}} block in: {Raw}", raw[..Math.Min(200, raw.Length)]);
            return s;
        }

        private static bool TryParseJsonObject(string s, out JsonObject? result)
        {
            try { result = JsonSerializer.Deserialize<JsonObject>(s, _jsonOptions); return result != null; }
            catch { result = null; return false; }
        }

        private static string RepairJson(string s)
        {
            var stack = new Stack<char>();
            var output = new StringBuilder();
            bool inString = false, escape = false;

            foreach (char c in s)
            {
                if (escape) { escape = false; output.Append(c); continue; }
                if (c == '\\' && inString) { escape = true; output.Append(c); continue; }
                if (c == '"') { inString = !inString; output.Append(c); continue; }
                if (inString) { output.Append(c); continue; }

                switch (c)
                {
                    case '{': stack.Push('}'); output.Append(c); break;
                    case '[': stack.Push(']'); output.Append(c); break;
                    case '}':
                    case ']':
                        if (stack.Count > 0 && stack.Peek() == c)
                        { stack.Pop(); output.Append(c); }
                        else if (stack.Count > 0)
                        { output.Append(stack.Pop()); }
                        break;
                    default: output.Append(c); break;
                }
            }

            while (stack.Count > 0) output.Append(stack.Pop());
            return output.ToString();
        }

        private static string? LongestBalancedJson(string s)
        {
            string? best = null;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '{') continue;
                int depth = 0; bool inStr = false, esc = false;
                for (int j = i; j < s.Length; j++)
                {
                    char c = s[j];
                    if (esc) { esc = false; continue; }
                    if (c == '\\' && inStr) { esc = true; continue; }
                    if (c == '"') { inStr = !inStr; continue; }
                    if (inStr) continue;
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']') depth--;
                    if (depth == 0)
                    {
                        var candidate = s[i..(j + 1)];
                        if (best == null || candidate.Length > best.Length) best = candidate;
                        break;
                    }
                }
            }
            return best;
        }

        // ════════════════════════════════════════════════════════════════════
        // ON-DEMAND: DESCRIPTION
        // ════════════════════════════════════════════════════════════════════

        public async Task<AiDescriptionResponseDto> GenerateDescriptionOnDemandAsync(SelectionGroupDto rootGroup)
        {
            var cacheKey = BuildCacheKey("desc_", rootGroup);
            if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
                return new AiDescriptionResponseDto { Description = cached, TokensUsed = 0, FromCache = true };

            var enriched = await EnrichGroupAsync(rootGroup);
            var prompt = SelectionPromptBuilder.BuildUserPrompt(enriched);
            var (description, tokens) = await CallGroqAsync(PromptTemplates.Description.System, prompt);

            _cache.Set(cacheKey, description, TimeSpan.FromHours(2));
            return new AiDescriptionResponseDto { Description = description, TokensUsed = tokens, FromCache = false };
        }

        // ════════════════════════════════════════════════════════════════════
        // ON-DEMAND: NAME
        // ════════════════════════════════════════════════════════════════════

        public async Task<string> GenerateNameOnDemandAsync(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return "Untitled Selection";

            var (name, _) = await CallGroqAsync(
                PromptTemplates.Description.NameSystem,
                PromptTemplates.Description.NameUser(description),
                maxTokens: 30, skipDelay: true);

            return name.Trim('"', '.', ' ');
        }

        // ════════════════════════════════════════════════════════════════════
        // ON-DEMAND: CONFIDENCE
        // ════════════════════════════════════════════════════════════════════

        public async Task<(int Score, int Tokens)> ScoreConfidenceOnDemandAsync(
            string intent, SelectionGroupDto rootGroup)
        {
            var catalog = await BuildNodeCatalogAsync();
            return await ScoreConfidenceAsync(intent, rootGroup, catalog);
        }
        private sealed class ResolvedClarificationAnswer
        {
            public string BlockId { get; set; } = "";
            public List<ClarificationRuleDto> ResolvedRules { get; set; } = new();
        }

        private static SelectionGroupDto BuildGroupFromResolvedAnswers(
            List<ResolvedClarificationAnswer> answers)
        {
            var meaningful = answers.Where(a => a.ResolvedRules.Count > 0).ToList();

            if (meaningful.Count == 0) return EmptyGroup();

            if (meaningful.Count == 1 && meaningful[0].ResolvedRules.Count == 1)
            {
                var r = meaningful[0].ResolvedRules[0];
                return new SelectionGroupDto
                {
                    LogicalOperator = "AND",
                    Rules = new() { new() { TreeNodeId = r.TreeNodeId, Operator = r.Operator, Value = r.Value } },
                    Groups = new()
                };
            }

            if (meaningful.Count == 1)
                return new SelectionGroupDto
                {
                    LogicalOperator = "OR",
                    Rules = meaningful[0].ResolvedRules.Select(r =>
                        new SelectionRuleDto { TreeNodeId = r.TreeNodeId, Operator = r.Operator, Value = r.Value }
                    ).ToList(),
                    Groups = new()
                };

            return new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new(),
                Groups = meaningful.Select(a => new SelectionGroupDto
                {
                    LogicalOperator = a.ResolvedRules.Count == 1 ? "AND" : "OR",
                    Rules = a.ResolvedRules.Select(r =>
                        new SelectionRuleDto { TreeNodeId = r.TreeNodeId, Operator = r.Operator, Value = r.Value }
                    ).ToList(),
                    Groups = new()
                }).ToList()
            };
        }
        /// <summary>
        /// Appends a new group as a sub-group of the existing root.
        /// The root's existing rules and groups are preserved exactly.
        /// </summary>
        private static SelectionGroupDto MergeGroupIntoRoot(
            SelectionGroupDto existingRoot, SelectionGroupDto newGroup)
        {
            var cloned = DeepCloneGroup(existingRoot);
            cloned.Groups ??= new List<SelectionGroupDto>();
            cloned.Groups.Add(newGroup);
            return cloned;
        }

    }   // ← closes AiService

    public sealed class NodeCatalogItem
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public string ParentName { get; set; } = ""; 
        public string NodeName { get; set; } = "";
        public string NodeDesc { get; set; } = "";
        public string DataType { get; set; } = "";
        public string Category { get; set; } = "";
        public string SearchText { get; set; } = "";
    }
}