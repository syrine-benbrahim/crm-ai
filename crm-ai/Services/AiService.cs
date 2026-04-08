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

        private const string DescriptionSystemPrompt =
        "You are a CRM marketing analyst assistant for a hospitality and retail business. " +
        "Your task is to read structured audience filter rules and produce a " +
        "clear, concise, professional audience description in plain English. " +
        "Write it as a single natural sentence like a marketer would describe the audience. " +
        "Do NOT use 'who are', 'and have', 'either...or' structures. " +
        "Example good style: 'Female customers aged 25-44 based in London or Manchester who visited in the last month.' " +
        "Return ONLY the description text — no JSON, no markdown, no preamble.";

        private sealed class NodeInfo
        {
            public int Id { get; set; }
            public string? NodeName { get; set; }
            public string? NodeDesc { get; set; }
            public string? DataType { get; set; }
            public string? EntityName { get; set; }
            public string? FieldName { get; set; }
            public string? ParentName { get; set; }
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
                return new AiDescriptionResponseDto
                {
                    Description = cached,
                    TokensUsed = 0,
                    FromCache = true
                };
            }

            var enrichedGroup = await EnrichGroupAsync(rootGroup);
            string userPrompt = SelectionPromptBuilder.BuildUserPrompt(enrichedGroup);

            var (description, tokensUsed) = await CallGroqAsync(DescriptionSystemPrompt, userPrompt);
            _cache.Set(cacheKey, description, TimeSpan.FromHours(2));

            return new AiDescriptionResponseDto
            {
                Description = description,
                TokensUsed = tokensUsed,
                FromCache = false
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. GENERATE SELECTION FROM PROMPT
        // ════════════════════════════════════════════════════════════════════

        public async Task<AiSelectionResponseDto> GenerateSelectionFromPromptAsync(
            string prompt,
            string? name = null)
        {
            _logger.LogInformation(
                "Generating selection from prompt | Length={Length}", prompt.Length);

            try
            {
                var catalog = await BuildNodeCatalogAsync();
                var systemPrompt = BuildSelectionSystemPrompt();

                var filteredCatalog = await FilterCatalogByPromptAiAsync(prompt, catalog);
                var userPrompt = BuildSelectionUserPrompt(prompt, filteredCatalog);

                // Use power model for the main build call
                var (rawJson, tokens1) = await CallGroqAsync(
                    systemPrompt, userPrompt, maxTokens: 2000,
                    model: _options.PowerModel);

                _logger.LogDebug("Raw AI selection JSON:\n{Json}", rawJson);

                var (rootGroup, unmatchedTerms, parseSuccess) =
                    ParseSelectionJson(rawJson, catalog);

                if (!parseSuccess)
                {
                    _logger.LogWarning("AI returned invalid JSON for selection generation");
                    return new AiSelectionResponseDto
                    {
                        Name = name ?? "AI Generated Selection",
                        Description = "Could not parse AI response. Please try again.",
                        RootGroup = new SelectionGroupDto
                        {
                            LogicalOperator = "AND",
                            Rules = new(),
                            Groups = new()
                        },
                        Confidence = 0,
                        UnmatchedTerms = new List<string> { "Parse error" },
                        TokensUsed = tokens1
                    };
                }

                var (confidence, tokens2) = await ScoreConfidenceAsync(prompt, rootGroup, catalog);

                var enrichedGroup = await EnrichGroupAsync(rootGroup);
                var enrichedPrompt = SelectionPromptBuilder.BuildUserPrompt(enrichedGroup);
                var (description, tokens3) = await CallGroqAsync(
                    DescriptionSystemPrompt, enrichedPrompt);

                string? selectionName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    selectionName = name;
                }
                else
                {
                    var (generatedName, _) = await CallGroqAsync(
                        "You are a CRM assistant. Generate a short (3-6 word) selection name. " +
                        "Return ONLY the name, no punctuation, no explanation.",
                        $"Audience description: {description}",
                        maxTokens: 30);
                    selectionName = generatedName.Trim('"', '.', ' ');
                }

                return new AiSelectionResponseDto
                {
                    Name = selectionName!,
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
                    Description = "The AI service is temporarily unavailable. " +
                                  "Please build your selection using the visual builder.",
                    RootGroup = new SelectionGroupDto
                    {
                        LogicalOperator = "AND",
                        Rules = new(),
                        Groups = new()
                    },
                    Confidence = 0,
                    UnmatchedTerms = new List<string> { "Service unavailable" },
                    TokensUsed = 0
                };
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Build full node catalog from DB
        // ────────────────────────────────────────────────────────────────────
        private async Task<Dictionary<int, NodeCatalogItem>> BuildNodeCatalogAsync()
        {
            const string cacheKey = "node_catalog_v1";

            if (_cache.TryGetValue(cacheKey, out Dictionary<int, NodeCatalogItem>? cached)
                && cached != null)
                return cached;

            var nodes = await _context.TreeNodes
                .Where(n => n.IsSelectable == 1)
                .Select(n => new NodeInfo
                {
                    Id = n.Id,
                    NodeName = n.NodeName,
                    NodeDesc = n.NodeDesc,
                    DataType = n.DataType,
                    EntityName = n.EntityName,
                    FieldName = n.FieldName,
                    ParentName = _context.TreeNodes
                        .Where(p => p.Id == n.ParentId)
                        .Select(p => p.NodeName)
                        .FirstOrDefault()
                })
                .ToListAsync();

            var catalog = nodes.ToDictionary(
                n => n.Id,
                n => new NodeCatalogItem
                {
                    Id = n.Id,
                    NodeName = n.NodeName ?? "",
                    NodeDesc = n.NodeDesc ?? n.NodeName ?? "",
                    DataType = n.DataType ?? "",
                    Category = n.ParentName ?? n.EntityName ?? "General",
                    SearchText = $"{n.NodeName} {n.NodeDesc} {n.ParentName} {n.DataType}".ToLower()
                });

            _cache.Set(cacheKey, catalog, TimeSpan.FromHours(1));
            _logger.LogInformation(
                "Built node catalog with {Count} selectable nodes", catalog.Count);
            return catalog;
        }

        // ────────────────────────────────────────────────────────────────────
        // OPTIMIZATION: AI-driven catalog filtering
        // ────────────────────────────────────────────────────────────────────
        private async Task<Dictionary<int, NodeCatalogItem>> FilterCatalogByPromptAiAsync(
            string prompt,
            Dictionary<int, NodeCatalogItem> fullCatalog)
        {
            try
            {
                var availableCategories = fullCatalog.Values
                    .Select(n => n.Category)
                    .Where(c => !string.IsNullOrWhiteSpace(c) && c != "General")
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();

                if (availableCategories.Count == 0)
                    return fullCatalog;

                var categoryList = string.Join(", ", availableCategories);

                var systemPrompt =
                    "You are a CRM filter assistant. " +
                    "Given a user's audience description and a list of filter category names, " +
                    "return ONLY the category names needed to fulfil the request. " +
                    "Return a JSON array of strings exactly matching names from the list. " +
                    "Example: [\"Location\", \"Gender\", \"Age\"]. " +
                    "Return ONLY the JSON array — no markdown, no explanation, no preamble.";

                var userPrompt =
                    $"Available categories: [{categoryList}]\n\n" +
                    $"User request: \"{prompt}\"\n\n" +
                    $"Which categories are needed? Return JSON array only.";

                var (responseJson, filterTokens) = await CallGroqAsync(
                    systemPrompt, userPrompt, maxTokens: 100, skipDelay: true);

                _logger.LogInformation(
                    "Category detection used {Tokens} tokens", filterTokens);

                var cleaned = CleanJson(responseJson);

                var neededCategories = JsonSerializer.Deserialize<List<string>>(
                    cleaned, _jsonOptions) ?? new List<string>();

                if (neededCategories.Count == 0)
                {
                    _logger.LogWarning(
                        "AI category detection returned empty list — using full catalog");
                    return fullCatalog;
                }

                var neededSet = neededCategories
                    .Select(c => c.Trim().ToLower())
                    .ToHashSet();

                _logger.LogInformation(
                    "AI detected categories: [{Categories}]",
                    string.Join(", ", neededCategories));

                var filtered = fullCatalog.Values
                    .Where(n => neededSet.Contains(n.Category.ToLower()))
                    .ToDictionary(n => n.Id);

                if (filtered.Count < 10)
                {
                    _logger.LogWarning(
                        "AI catalog filter too narrow ({Count} nodes) — " +
                        "falling back to full catalog", filtered.Count);
                    return fullCatalog;
                }

                _logger.LogInformation(
                    "Catalog filtered: {Full} → {Filtered} nodes (saved ~{Saved} tokens)",
                    fullCatalog.Count, filtered.Count,
                    (fullCatalog.Count - filtered.Count) * 8);

                return filtered;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AI catalog filtering failed — falling back to full catalog");
                return fullCatalog;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // System prompt for selection generation (with few-shot examples)
        // ────────────────────────────────────────────────────────────────────
        private static string BuildSelectionSystemPrompt() => """
            You are a CRM selection builder AI for a hospitality and retail business.
            Your task is to convert a plain-English audience description into a structured
            JSON rule tree using ONLY the TreeNode IDs provided in the catalog.

            RULES:
            - Use ONLY TreeNode IDs from the provided catalog. NEVER invent IDs.
            - logicalOperator must be "AND", "OR", or "EXCLUDE".
            - Use "AND" when all conditions must be true.
            - Use "OR" when any condition can be true (e.g. age ranges, multiple cities).
            - Use "EXCLUDE" to exclude a group of customers.
            - operator is always "=" and value is always "" for standard nodes.
            - Return ONLY valid JSON. No markdown, no explanation, no preamble.

            CRITICAL TIME MAPPING RULES:
            - "last week" or "past week" or "within 7 days" or "in the last 7 days" = ID 5350 ONLY. Never use 5349 for this.
            - "yesterday" = ID 5349 ONLY if user explicitly says the word "yesterday".
            - "last 2 weeks" or "past 2 weeks" = ID 5351 (8-14 days).
            - "last month" or "last 30 days" or "past month" = ID 5352 (15-31 days).
            - "last 2 months" = ID 5353 (1-2 months).
            - "last 3 months" = ID 5354 (2-3 months).

            CRITICAL SPEND MAPPING RULES:
            - "over £X" or "more than £X" or "above £X" or "spent over £X" means ALL spend
              ranges above that value. Wrap them ALL in an OR group.
              Example "over £50" from average transaction value:
              OR group → IDs 5365(£50-£60), 5366(£60-£70), 5367(£70-£80), 5368(£80-£90), 5369(£90+)
              Example "over £50" from total transaction value:
              OR group → IDs 5376(£50-£75), 5377(£75-£100), 5378(£100-£150), 5379(£150-£200),
                         5380(£200-£400), 5381(£400-£600), 5382(£600+)
            - "under £X" means ALL spend ranges below X. Wrap in OR group.
            - A specific range like "£20-£30" means just that one node.

            CRITICAL AGE MAPPING RULES:
            - "aged 25-34" = ONLY ID 5. Do NOT add adjacent ranges.
            - "aged 18-44" = IDs 4(18-24) + 5(25-34) + 6(35-44) in an OR group.
            - "aged 25-44" = IDs 5(25-34) + 6(35-44) in an OR group.
            - "under 18" = ID 5467.
            - "over 65" or "65+" = ID 9.
            - "young customers" = IDs 4(18-24) + 5(25-34) in an OR group.

            CRITICAL LOYALTY MAPPING RULES:
            - "loyal" = ID 5503 ONLY
            - "frequent" = ID 5504 ONLY
            - "occasional" = ID 5505 ONLY
            - "infrequent" = ID 5506 ONLY — use ONLY when user explicitly says "infrequent"
            - "lapsed" = ID 5507 ONLY — NEVER map "lapsed" to infrequent
            - "long-term lapsed" = ID 5508 ONLY
            - "never visited" or "never" = ID 5509 ONLY
            - Do NOT confuse "lapsed" (5507) with "infrequent" (5506). They are different.

            OUTPUT FORMAT:
            {
              "rootGroup": {
                "logicalOperator": "AND",
                "rules": [
                  { "treeNodeId": 123, "operator": "=", "value": "" }
                ],
                "groups": [
                  {
                    "logicalOperator": "OR",
                    "rules": [
                      { "treeNodeId": 456, "operator": "=", "value": "" }
                    ],
                    "groups": []
                  }
                ]
              }
            }

            FEW-SHOT EXAMPLES:

            EXAMPLE 1:
            User: "Female customers"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""}],"groups":[]}}

            EXAMPLE 2:
            User: "Male or female customers aged 25 to 44"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":13,"operator":"=","value":""},{"treeNodeId":14,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"OR","rules":[{"treeNodeId":5,"operator":"=","value":""},{"treeNodeId":6,"operator":"=","value":""}],"groups":[]}]}}

            EXAMPLE 3:
            User: "Customers in London who visited last week and spent over £90"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":5369,"operator":"=","value":""}],"groups":[]}]}}

            EXAMPLE 4:
            User: "Loyal customers in London or Manchester excluding long-term lapsed"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":5503,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5273,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5508,"operator":"=","value":""}],"groups":[]}]}}

            EXAMPLE 5:
            User: "Emailable female customers aged 18-34 in Scotland who visited 1-2 months ago"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":182,"operator":"=","value":""},{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5279,"operator":"=","value":""},{"treeNodeId":5353,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":4,"operator":"=","value":""},{"treeNodeId":5,"operator":"=","value":""}],"groups":[]}]}}

            EXAMPLE 6:
            User: "Female customers in London aged 25-34 who visited last week"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""},{"treeNodeId":5,"operator":"=","value":""}],"groups":[]}}

            EXAMPLE 7:
            User: "Customers who spent over £50 in the last month"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":5352,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":5376,"operator":"=","value":""},{"treeNodeId":5377,"operator":"=","value":""},{"treeNodeId":5378,"operator":"=","value":""},{"treeNodeId":5379,"operator":"=","value":""},{"treeNodeId":5380,"operator":"=","value":""},{"treeNodeId":5381,"operator":"=","value":""},{"treeNodeId":5382,"operator":"=","value":""}],"groups":[]}]}}

            EXAMPLE 8:
            User: "Loyal or frequent customers in London or Manchester aged 18-44 who visited in the last 30 days and spent over £50, excluding long-term lapsed and never visited"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":5352,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":5503,"operator":"=","value":""},{"treeNodeId":5504,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"OR","rules":[{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5273,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"OR","rules":[{"treeNodeId":4,"operator":"=","value":""},{"treeNodeId":5,"operator":"=","value":""},{"treeNodeId":6,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"OR","rules":[{"treeNodeId":5376,"operator":"=","value":""},{"treeNodeId":5377,"operator":"=","value":""},{"treeNodeId":5378,"operator":"=","value":""},{"treeNodeId":5379,"operator":"=","value":""},{"treeNodeId":5380,"operator":"=","value":""},{"treeNodeId":5381,"operator":"=","value":""},{"treeNodeId":5382,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5508,"operator":"=","value":""},{"treeNodeId":5509,"operator":"=","value":""}],"groups":[]}]}}

            EXAMPLE 9:
            User: "Female customers in London aged 25-34 who visited last week, excluding lapsed"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""},{"treeNodeId":5,"operator":"=","value":""}],"groups":[{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5507,"operator":"=","value":""}],"groups":[]}]}}

            EXAMPLE 10:
            User: CURRENT RULES:
            [AND]
              • Contact: Emailable
              • Visit Recency: <= 7 days
              [AND]
                • Gender: Female
            USER WANTS TO CHANGE: "or another group where male who are aged over 65"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":182,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"AND","rules":[{"treeNodeId":13,"operator":"=","value":""},{"treeNodeId":9,"operator":"=","value":""}],"groups":[]}]}}
            """;

        // ────────────────────────────────────────────────────────────────────
        // User prompt: filtered catalog + actual user request
        // ────────────────────────────────────────────────────────────────────
        private static string BuildSelectionUserPrompt(
            string userPrompt,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== AVAILABLE FILTERS (use ONLY these IDs) ===");
            sb.AppendLine();

            var grouped = catalog.Values
                .GroupBy(n => n.Category)
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                sb.Append($"[{group.Key}]: ");
                sb.AppendLine(string.Join(" | ", group.Select(n => $"ID={n.Id} \"{n.NodeDesc}\"")));
            }

            sb.AppendLine();
            sb.AppendLine("=== USER REQUEST ===");
            sb.AppendLine(userPrompt);
            sb.AppendLine();
            sb.AppendLine("Build the JSON rule tree now:");

            return sb.ToString();
        }

        // ────────────────────────────────────────────────────────────────────
        // Confidence scoring
        // ────────────────────────────────────────────────────────────────────
        private async Task<(int Score, int Tokens)> ScoreConfidenceAsync(
            string originalPrompt,
            SelectionGroupDto rootGroup,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            try
            {
                var readableRules = DescribeGroup(rootGroup, catalog);

                var systemPrompt =
                    "You are a CRM QA assistant. Evaluate if the generated selection rules " +
                    "correctly match the user's intent. " +
                    "Return ONLY a JSON object: {\"score\": <0-100>, \"issues\": [\"issue1\", \"issue2\"]}";

                var userPrompt =
                    $"User wanted: \"{originalPrompt}\"\n\n" +
                    $"Generated rules:\n{readableRules}\n\n" +
                    $"Score how well the rules match the intent (0=completely wrong, 100=perfect match).";

                var (responseJson, tokens) = await CallGroqAsync(
                    systemPrompt, userPrompt, maxTokens: 200);

                var cleaned = CleanJson(responseJson);
                var scoreObj = JsonSerializer.Deserialize<JsonObject>(cleaned, _jsonOptions);
                var score = scoreObj?["score"]?.GetValue<int>() ?? 70;

                return (Math.Clamp(score, 0, 100), tokens);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Confidence scoring failed, defaulting to 70");
                return (70, 0);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Parse and validate AI JSON response
        // ────────────────────────────────────────────────────────────────────
        private (SelectionGroupDto Group, List<string> UnmatchedTerms, bool Success)
            ParseSelectionJson(string rawJson, Dictionary<int, NodeCatalogItem> catalog)
        {
            try
            {
                var cleaned = CleanJson(rawJson);
                var parsed = JsonSerializer.Deserialize<JsonObject>(cleaned, _jsonOptions);

                if (parsed == null)
                    return (new SelectionGroupDto(), new(), false);

                var rootGroupNode = parsed["rootGroup"] ?? parsed["RootGroup"];
                if (rootGroupNode == null)
                    return (new SelectionGroupDto(), new(), false);

                var rootGroup = JsonSerializer.Deserialize<SelectionGroupDto>(
                    rootGroupNode.ToJsonString(), _jsonOptions);

                if (rootGroup == null)
                    return (new SelectionGroupDto(), new(), false);

                var unmatchedTerms = new List<string>();
                ValidateNodeIds(rootGroup, catalog, unmatchedTerms);

                return (rootGroup, unmatchedTerms, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse AI selection JSON: {Json}", rawJson);
                return (new SelectionGroupDto(), new() { "JSON parse error" }, false);
            }
        }

        private void ValidateNodeIds(
            SelectionGroupDto group,
            Dictionary<int, NodeCatalogItem> catalog,
            List<string> unmatched)
        {
            if (group.Rules != null)
            {
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
            }

            if (group.Groups != null)
                foreach (var child in group.Groups)
                    ValidateNodeIds(child, catalog, unmatched);
        }

        private static string DescribeGroup(
            SelectionGroupDto group,
            Dictionary<int, NodeCatalogItem> catalog,
            int depth = 0)
        {
            var sb = new StringBuilder();
            var pad = new string(' ', depth * 2);
            sb.AppendLine($"{pad}[{group.LogicalOperator}]");

            if (group.Rules != null)
                foreach (var rule in group.Rules)
                {
                    var name = catalog.TryGetValue(rule.TreeNodeId, out var node)
                        ? $"{node.Category}: {node.NodeDesc}"
                        : $"Unknown({rule.TreeNodeId})";
                    sb.AppendLine($"{pad}  • {name}");
                }

            if (group.Groups != null)
                foreach (var child in group.Groups)
                    sb.Append(DescribeGroup(child, catalog, depth + 1));

            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. VALIDATE SELECTION
        // ════════════════════════════════════════════════════════════════════

        public async Task<AiValidationResponseDto> ValidateSelectionAsync(
            SelectionGroupDto rootGroup)
        {
            _logger.LogInformation("Validating selection rules with AI");

            var catalog = await BuildNodeCatalogAsync();
            var readableRules = DescribeGroup(rootGroup, catalog);

            var totalRules = CountRules(rootGroup);
            if (totalRules == 0)
            {
                return new AiValidationResponseDto
                {
                    Summary = "Your selection has no rules.",
                    Status = "error",
                    Issues = new List<ValidationIssue>
                    {
                        new()
                        {
                            Severity = "error",
                            Title = "Empty selection",
                            Detail = "You have not added any rules. Add at least one filter before saving."
                        }
                    },
                    TokensUsed = 0
                };
            }

            var systemPrompt = """
                Return raw JSON only. Do not use markdown code fences.
                You are a CRM selection validator for a hospitality and retail business.
                Your job is to analyse a set of audience filter rules and:
                1. Write a plain-English summary of what the selection targets.
                2. Identify any logical issues, impossible combinations, or missing best-practice filters.

                COMMON ISSUES TO LOOK FOR:
                - Age ranges in an AND group → impossible (customer can't be two ages at once). Should be OR.
                - Gender values in an AND group → impossible. Should be OR.
                - Location values in an AND group → usually means OR was intended.
                - Loyalty segments in an AND group → usually OR was intended.
                - Spend ranges in an AND group → impossible. Should be OR.
                - EXCLUDE group with no AND/OR rules to exclude from → pointless exclusion.
                - Selection targets email campaign but no email availability filter (ID 182 or 303).
                - Very broad selection with no filters at all → will match everyone.
                - Contradictory rules (e.g. include Loyal AND exclude Loyal).

                SEVERITY LEVELS:
                - "error": logically impossible, selection will return 0 results
                - "warning": not impossible but likely unintended or missing best practice

                Return ONLY a valid JSON object in this exact format:
                {
                  "summary": "Plain-English description of what this selection targets.",
                  "status": "valid or warning or error",
                  "issues": [
                    {
                      "severity": "warning or error",
                      "title": "Short issue title",
                      "detail": "Full explanation and how to fix it."
                    }
                  ]
                }

                If there are no issues, return "issues": [] and "status": "valid".
                """;

            var userPrompt =
                $"Here are the selection rules to validate:\n\n{readableRules}\n\n" +
                $"Analyse them and return the JSON validation result.";

            var (responseJson, tokensUsed) = await CallGroqAsync(
                systemPrompt, userPrompt, maxTokens: 1000);

            try
            {
                var cleaned = CleanJson(responseJson);
                var parsed = JsonSerializer.Deserialize<JsonObject>(cleaned, _jsonOptions);

                if (parsed == null)
                    throw new Exception("Null response from AI");

                var summary = parsed["summary"]?.GetValue<string>()
                    ?? "Could not summarise selection.";
                var status = parsed["status"]?.GetValue<string>() ?? "valid";

                var issues = new List<ValidationIssue>();
                var issuesArray = parsed["issues"]?.AsArray();

                if (issuesArray != null)
                {
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
                }

                return new AiValidationResponseDto
                {
                    Summary = summary,
                    Status = status,
                    Issues = issues,
                    TokensUsed = tokensUsed
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse AI validation response: {Json}", responseJson);
                return new AiValidationResponseDto
                {
                    Summary = "Could not complete validation. Please try again.",
                    Status = "warning",
                    Issues = new List<ValidationIssue>
                    {
                        new()
                        {
                            Severity = "warning",
                            Title    = "Validation unavailable",
                            Detail   = "AI validation could not be completed at this time."
                        }
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
        // 4. CONVERSATIONAL FLOW
        // ════════════════════════════════════════════════════════════════════

        public async Task<ConversationResponseDto> ContinueConversationAsync(
            ConversationRequestDto request)
        {
            _logger.LogInformation(
                "Conversation turn — {Count} messages, hasExistingRules={HasRules}",
                request.Messages.Count,
                request.CurrentRootGroup != null);

            try
            {
                if (request.Messages == null || !request.Messages.Any())
                    return new ConversationResponseDto
                    {
                        Status = "error",
                        Message = "No messages provided.",
                        TokensUsed = 0
                    };

                var catalog = await BuildNodeCatalogAsync();

                var existingRulesContext = request.CurrentRootGroup != null
                    ? $"\n\nEXISTING RULES THE USER HAS BUILT:\n" +
                      $"{DescribeGroup(request.CurrentRootGroup, catalog)}\n" +
                      "When the user says 'also', 'add', 'remove', 'change', 'exclude', " +
                      "'instead', 'make it wider', 'actually' — " +
                      "REFINE these existing rules accordingly.\n"
                    : string.Empty;

                // ── CONFIRMATION SHORTCUT ────────────────────────────────────
                // If the frontend sends Confirmed=true, the user approved the
                // pending_confirmation card — skip rebuild entirely, just generate
                // a fresh description and return completed.
                if (request.Confirmed == true && request.CurrentRootGroup != null)
                {
                    _logger.LogInformation("User confirmed selection — skipping rebuild");
                    var enrichedForConfirm = await EnrichGroupAsync(request.CurrentRootGroup);
                    var enrichedPromptForConfirm = SelectionPromptBuilder.BuildUserPrompt(enrichedForConfirm);
                    var (confirmedDescription, descTokens) = await CallGroqAsync(
                        DescriptionSystemPrompt, enrichedPromptForConfirm);

                    return new ConversationResponseDto
                    {
                        Status = "completed",
                        Message = "Selection confirmed! Review the rules and save when ready.",
                        Questions = new(),
                        Selection = new AiSelectionResponseDto
                        {
                            Name = request.Name ?? "AI Generated Selection",
                            Description = confirmedDescription,
                            RootGroup = request.CurrentRootGroup,
                            Confidence = 100,
                            UnmatchedTerms = new(),
                            TokensUsed = descTokens
                        },
                        TokensUsed = descTokens
                    };
                }

                var (action, tokens1) = await DecideActionAsync(request.Messages);

                _logger.LogInformation("AI decided action: {Action}", action);

                return action.ToLower() switch
                {
                    "build" when request.IntentConfirmed != true =>
                        await HandleIntentConfirmationAsync(
                            request.Messages, existingRulesContext, tokens1),

                    "build" =>
                        await HandleBuildOrRefineAsync(
                            request.Messages, request.CurrentRootGroup,
                            catalog, existingRulesContext, request.Name, tokens1),

                    _ => await HandleAskAsync(
                        request.Messages, existingRulesContext, tokens1)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in ContinueConversationAsync");
                return new ConversationResponseDto
                {
                    Status = "error",
                    Message = "The AI service is temporarily unavailable. " +
                              "Please build your selection using the visual builder.",
                    TokensUsed = 0
                };
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // DECISION ENGINE
        // ────────────────────────────────────────────────────────────────────
        private async Task<(string Action, int Tokens)> DecideActionAsync(
            List<ConversationMessage> messages)
        {
            try
            {
                var lastUserMessage = messages
                    .LastOrDefault(m => m.Role == "user")?.Content ?? "";

                var systemPrompt =
                    "You are a CRM assistant decision engine. " +
                    "Your only job is to decide if a user message contains enough specific " +
                    "filter information to build or refine a CRM audience selection, " +
                    "or if you need to ask for more details first.\n\n" +
                    "Return ONLY one word: \"build\" or \"ask\".\n\n" +
                    "Return \"build\" if the message contains at least one specific filter like:\n" +
                    "- A specific city or region (London, Manchester, Scotland...)\n" +
                    "- A gender (female, male, women, men)\n" +
                    "- An age range (aged 25-34, above 60, young customers, over 65...)\n" +
                    "- A visit timeframe (last week, last month, yesterday, recently...)\n" +
                    "- A spend amount (over £50, spent £100, high value...)\n" +
                    "- A loyalty segment (loyal, lapsed, frequent, occasional...)\n" +
                    "- A contact filter (emailable, smsable, mailable)\n" +
                    "- A refinement action with a specific target " +
                    "(add emailable, remove age, exclude lapsed, also Manchester...)\n" +
                    "- Adding a new group (or another group where...)\n\n" +
                    "Return \"ask\" if the message is too vague, nonsensical, " +
                    "a single generic word, or missing all specific filter details.\n\n" +
                    "Examples:\n" +
                    "\"Female customers in London\" → build\n" +
                    "\"Loyal customers\" → build\n" +
                    "\"Customers who visited last week\" → build\n" +
                    "\"and above 60\" → build\n" +
                    "\"also add emailable\" → build\n" +
                    "\"exclude lapsed\" → build\n" +
                    "\"remove the age filter\" → build\n" +
                    "\"also Manchester\" → build\n" +
                    "\"or another group where male aged over 65\" → build\n" +
                    "\"visits also\" → ask\n" +
                    "\"what about the visits\" → ask\n" +
                    "\"Show me some people\" → ask\n" +
                    "\"location\" → ask\n" +
                    "\"audience\" → ask\n" +
                    "\"hhhhh\" → ask\n" +
                    "\"I want customers\" → ask\n" +
                    "Return ONLY the single word. No explanation.";

                var (response, tokens) = await CallGroqAsync(
                    systemPrompt,
                    $"User message: \"{lastUserMessage}\"",
                    maxTokens: 10,
                    skipDelay: true);

                var action = response.Trim().ToLower().Contains("build") ? "build" : "ask";

                _logger.LogInformation(
                    "Intent detection: \"{Message}\" → {Action} ({Tokens} tokens)",
                    lastUserMessage.Length > 50
                        ? lastUserMessage[..50] + "..."
                        : lastUserMessage,
                    action, tokens);

                return (action, tokens);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intent detection failed — defaulting to ask");
                return ("ask", 0);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // ASK — generate clarifying questions
        // ────────────────────────────────────────────────────────────────────
        private async Task<ConversationResponseDto> HandleAskAsync(
            List<ConversationMessage> messages,
            string existingRulesContext,
            int previousTokens)
        {
            var systemPrompt =
                "You are a friendly CRM selection builder assistant.\n" +
                "The user wants to build an audience selection but their request is unclear.\n" +
                existingRulesContext +
                "\nAsk ONLY the questions needed to clarify the vague parts.\n" +
                "DO NOT ask about things that are already clear.\n" +
                "Ask maximum 3 questions, minimum 1. Be concise and friendly.\n" +
                "\nAvailable filter categories:\n" +
                "- Location: specific UK cities or regions\n" +
                "- Age: age ranges (18-24, 25-34, 35-44, 45-54, 55-64, 65+)\n" +
                "- Gender: Male, Female\n" +
                "- Visit recency: yesterday, last 7 days, 8-14 days, 15-31 days, 1-2 months\n" +
                "- Spend: average or total transaction value in GBP\n" +
                "- Loyalty: Loyal, Frequent, Occasional, Infrequent, Lapsed, Long-term lapsed\n" +
                "- Contact preference: Emailable, SMSable, Mailable\n" +
                "\nReturn ONLY a JSON object:\n" +
                "{\"message\": \"Friendly intro sentence.\", " +
                "\"questions\": [\"Question 1?\", \"Question 2?\"]}";

            var conversationText = string.Join("\n",
                messages.TakeLast(6).Select(m => $"{m.Role}: {m.Content}"));

            var (responseJson, tokens) = await CallGroqAsync(
                systemPrompt,
                $"Conversation:\n{conversationText}\n\nGenerate clarifying questions:",
                maxTokens: 300);

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonObject>(
                    CleanJson(responseJson), _jsonOptions);

                var message = parsed?["message"]?.GetValue<string>()
                    ?? "I need a few more details to build your selection:";
                var questions = parsed?["questions"]?.AsArray()
                    .Select(q => q?.GetValue<string>() ?? "")
                    .Where(q => !string.IsNullOrWhiteSpace(q))
                    .ToList() ?? new List<string>();

                return new ConversationResponseDto
                {
                    Status = "clarifying",
                    Message = message,
                    Questions = questions,
                    Selection = null,
                    TokensUsed = previousTokens + tokens
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse clarifying questions response");
                return new ConversationResponseDto
                {
                    Status = "clarifying",
                    Message = "Could you give me a bit more detail?",
                    Questions = new List<string>
                    {
                        "Which cities or regions are you targeting?",
                        "Any specific age group or gender?",
                        "Any spend or visit recency requirement?"
                    },
                    TokensUsed = previousTokens + tokens
                };
            }
        }

        private async Task<ConversationResponseDto> HandleIntentConfirmationAsync(
    List<ConversationMessage> messages,
    string existingRulesContext,
    int previousTokens)
        {
            var lastUserMessage = messages
                .LastOrDefault(m => m.Role == "user")?.Content ?? "";

            var systemPrompt =
                "You are a CRM selection builder assistant. " +
                "Before building the audience selection, confirm your understanding of what the user wants.\n" +
                existingRulesContext +
                "\nWrite a short, friendly 1–2 sentence summary of exactly what you understood. " +
                "Be specific: mention the filters you detected (locations, age, gender, spend, recency, loyalty, etc.). " +
                "End with 'Shall I build this selection?' — nothing else.\n" +
                "Return ONLY a JSON object: {\"summary\": \"Your summary here. Shall I build this selection?\"}";

            var conversationText = string.Join("\n",
                messages.TakeLast(6).Select(m => $"{m.Role}: {m.Content}"));

            var (responseJson, tokens) = await CallGroqAsync(
                systemPrompt,
                $"Conversation:\n{conversationText}\n\nSummarise your understanding:",
                maxTokens: 200);

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonObject>(
                    CleanJson(responseJson), _jsonOptions);

                var summary = parsed?["summary"]?.GetValue<string>()
                    ?? $"I'll build a selection based on: {lastUserMessage}. Shall I build this selection?";

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
                    Message = $"I understood you want: {lastUserMessage}. Shall I build this selection?",
                    Questions = new(),
                    Selection = null,
                    TokensUsed = previousTokens + tokens
                };
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // BUILD / REFINE
        //
        // KEY CHANGE: When refining (currentRootGroup != null), we do NOT pass
        // the full conversation history to the AI. Instead we pass:
        //   - The current rules in readable form
        //   - ONLY the latest user message as the delta instruction
        // This prevents the AI from reinterpreting old messages and losing context.
        // ────────────────────────────────────────────────────────────────────
        private async Task<ConversationResponseDto> HandleBuildOrRefineAsync(
            List<ConversationMessage> messages,
            SelectionGroupDto? currentRootGroup,
            Dictionary<int, NodeCatalogItem> catalog,
            string existingRulesContext,
            string? name,
            int previousTokens)
        {
            bool isRefine = currentRootGroup != null && CountRules(currentRootGroup) > 0;

            string conversationSummary;

            if (isRefine)
            {
                // REFINE: collapse into one clear atomic instruction.
                // The AI sees current rules + one specific change — nothing else.
                var latestUserMessage = messages
                    .LastOrDefault(m => m.Role == "user")?.Content ?? "";

                var currentRulesDescription = DescribeGroup(currentRootGroup!, catalog);

                conversationSummary =
                    $"CURRENT RULES:\n{currentRulesDescription}\n\n" +
                    $"USER WANTS TO CHANGE: \"{latestUserMessage}\"\n\n" +
                    $"Apply ONLY this change to the existing rules. " +
                    $"Keep everything else exactly the same.";

                _logger.LogInformation(
                    "Refine mode — passing current rules + delta only. Delta: \"{Delta}\"",
                    latestUserMessage);
            }
            else
            {
                // BUILD: use full conversation so context from earlier messages is preserved
                conversationSummary = string.Join("\n",
                    messages.TakeLast(8).Select(m => $"{m.Role}: {m.Content}"));
            }

            var systemPrompt = BuildConversationalBuildPrompt(existingRulesContext, isRefine);

            var filteredCatalog = await FilterCatalogByPromptAiAsync(
                conversationSummary, catalog);
            var userPrompt = BuildSelectionUserPrompt(conversationSummary, filteredCatalog);

            // Use PowerModel for the main rule tree build
            var (rawJson, tokens1) = await CallGroqAsync(
                systemPrompt, userPrompt, maxTokens: 2000,
                model: _options.PowerModel);

            var (rootGroup, unmatchedTerms, parseSuccess) =
                ParseSelectionJson(rawJson, catalog);

            if (!parseSuccess)
            {
                _logger.LogWarning("Build/refine failed to parse JSON");
                return new ConversationResponseDto
                {
                    Status = "error",
                    Message = "I couldn't build the selection from our conversation. " +
                              "Could you try rephrasing?",
                    TokensUsed = previousTokens + tokens1
                };
            }

            var (confidence, tokens2) = await ScoreConfidenceAsync(
                conversationSummary, rootGroup, catalog);

            if (confidence < 30 && CountRules(rootGroup) == 0)
            {
                _logger.LogWarning(
                    "Build produced empty rule tree with confidence {Score} — routing to ask",
                    confidence);
                return new ConversationResponseDto
                {
                    Status = "clarifying",
                    Message = "I couldn't quite understand that request. " +
                              "Could you give me a bit more detail?",
                    Questions = new List<string>
                    {
                        "Which cities or regions are you targeting?",
                        "Any specific age group or gender?",
                        "Any visit recency or spend requirement?"
                    },
                    TokensUsed = previousTokens + tokens1 + tokens2
                };
            }

            var enrichedGroup = await EnrichGroupAsync(rootGroup);
            var enrichedPrompt = SelectionPromptBuilder.BuildUserPrompt(enrichedGroup);
            var (description, tokens3) = await CallGroqAsync(
                DescriptionSystemPrompt, enrichedPrompt);

            string? selectionName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                selectionName = name;
            }
            else
            {
                var (generatedName, _) = await CallGroqAsync(
                    "You are a CRM assistant. Generate a short (3-6 word) selection name. " +
                    "Return ONLY the name, no punctuation, no explanation.",
                    $"Audience description: {description}",
                    maxTokens: 30);
                selectionName = generatedName.Trim('"', '.', ' ');
            }

            var selection = new AiSelectionResponseDto
            {
                Name = selectionName!,
                Description = description,
                RootGroup = rootGroup,
                Confidence = confidence,
                UnmatchedTerms = unmatchedTerms,
                TokensUsed = tokens1 + tokens2 + tokens3
            };

            // Return pending_confirmation so the user can verify before committing.
            // The frontend shows the description + two buttons:
            //   "Looks good" → sends Confirmed=true → returns "completed"
            //   "Change it"  → user types a new message → normal refine flow
            return new ConversationResponseDto
            {
                Status = "pending_confirmation",
                Message = isRefine
                    ? $"I've updated your selection:\n\n{description}\n\nDoes this look correct?"
                    : $"Here's what I've built:\n\n{description}\n\nDoes this look correct?",
                Questions = new(),
                Selection = selection,
                TokensUsed = previousTokens + tokens1 + tokens2 + tokens3
            };
        }

        // ────────────────────────────────────────────────────────────────────
        // Build the system prompt for conversational build/refine
        // ────────────────────────────────────────────────────────────────────
        private static string BuildConversationalBuildPrompt(
            string existingRulesContext,
            bool isRefine = false)
        {
            var basePrompt = BuildSelectionSystemPrompt();

            if (isRefine)
            {
                return basePrompt + """


                    ADDITIONAL REFINE RULES:
                    - You are given the CURRENT rules and ONE specific change to apply.
                    - Keep ALL existing rules exactly as they are.
                    - ONLY add or remove the specific thing mentioned.
                    - Do NOT reorder, reinterpret, or rebuild the whole tree from scratch.
                    - If user says "add female", add the Female node to the root AND rules.
                    - If user says "or another group where male aged over 65", add a NEW sub-group
                      with AND operator containing Male + 65+ nodes. Do NOT change anything else.
                    - If user says "remove age", remove only the age node(s), nothing else.
                    - If user says "exclude lapsed", add an EXCLUDE group with the lapsed node.
                    - If user says "and who are also females", add a new AND sub-group with Female.
                    """;
            }

            return basePrompt + """


                ADDITIONAL BUILD RULES:
                """ +
                existingRulesContext +
                """

                - Read the FULL conversation history to understand the complete intent.
                - The user may have clarified vague terms in follow-up messages.
                  e.g. 'big cities' clarified as 'London and Manchester' in next message.
                - Use ALL information gathered across the conversation.
                """;
        }

        // ════════════════════════════════════════════════════════════════════
        // SHARED: Groq API call
        //
        // model: null = use _options.Model (fast default)
        //        set  = use that specific model (e.g. PowerModel for build calls)
        // skipDelay: true for tiny calls (intent detection, category detection)
        // ════════════════════════════════════════════════════════════════════
        private async Task<(string Response, int TokensUsed)> CallGroqAsync(
            string systemPrompt,
            string userPrompt,
            int maxTokens = 300,
            bool skipDelay = false,
            string? model = null)
        {
            var request = new GrokRequest
            {
                Model = model ?? _options.Model,
                Temperature = 0.1f,
                MaxTokens = maxTokens,
                Messages = new List<GrokMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user",   Content = userPrompt   }
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (!skipDelay)
                await Task.Delay(500);

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await _httpClient.PostAsync(
                    "https://api.groq.com/openai/v1/chat/completions", content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP error calling Groq API");
                throw new InvalidOperationException("AI service is currently unavailable.", ex);
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync();
                _logger.LogError("Groq API error {Status}: {Body}",
                    httpResponse.StatusCode, errorBody);
                throw new InvalidOperationException(
                    $"AI service returned {httpResponse.StatusCode}. Detail: {errorBody}");
            }

            var responseJson = await httpResponse.Content.ReadAsStringAsync();
            var grokResponse = JsonSerializer.Deserialize<GrokResponse>(responseJson);

            var responseText = grokResponse?.Choices?.FirstOrDefault()?.Message?.Content?.Trim()
                ?? "";
            var tokensUsed = grokResponse?.Usage?.TotalTokens ?? 0;

            _logger.LogInformation(
                "Groq API call complete ({Tokens} tokens, model={Model})",
                tokensUsed, request.Model);
            return (responseText, tokensUsed);
        }

        // ════════════════════════════════════════════════════════════════════
        // SHARED: Enrichment for description generation
        // ════════════════════════════════════════════════════════════════════

        private async Task<EnrichedGroupDto> EnrichGroupAsync(SelectionGroupDto group)
        {
            var allIds = CollectNodeIds(group);

            var nodes = await _context.TreeNodes
                .Where(n => allIds.Contains(n.Id))
                .Select(n => new NodeInfo
                {
                    Id = n.Id,
                    NodeName = n.NodeName,
                    NodeDesc = n.NodeDesc,
                    DataType = n.DataType,
                    EntityName = n.EntityName,
                    FieldName = n.FieldName,
                    ParentName = _context.TreeNodes
                        .Where(p => p.Id == n.ParentId)
                        .Select(p => p.NodeName)
                        .FirstOrDefault()
                })
                .ToDictionaryAsync(n => n.Id);

            return CloneEnriched(group, nodes);
        }

        private static EnrichedGroupDto CloneEnriched(
            SelectionGroupDto group,
            Dictionary<int, NodeInfo> nodeMap)
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

                    return new EnrichedRuleDto
                    {
                        TreeNodeId = r.TreeNodeId,
                        NodeName = !string.IsNullOrWhiteSpace(node.NodeDesc)
                            ? node.NodeDesc! : node.NodeName ?? $"Node {r.TreeNodeId}",
                        Category = !string.IsNullOrWhiteSpace(node.ParentName)
                            ? node.ParentName! : node.EntityName ?? "Customer",
                        DataType = node.DataType,
                        Value = r.Value
                    };
                }).ToList(),
                Groups = group.Groups?
                    .Select(g => CloneEnriched(g, nodeMap))
                    .ToList()
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // 5. CHECK INTENT
        // ════════════════════════════════════════════════════════════════════

        public async Task<IntentCheckResponseDto> CheckIntentAsync(IntentCheckRequestDto request)
        {
            _logger.LogInformation("Checking intent: \"{Intent}\"", request.Intent);

            var catalog = await BuildNodeCatalogAsync();
            var readableRules = DescribeGroup(request.RootGroup, catalog);

            // ── Step 1: compare intent vs rules ─────────────────────────────
            var systemPrompt = """
        You are a CRM selection auditor for a hospitality and retail business.
        You are given:
        1. A user's stated intention (what they wanted to build)
        2. The actual rules they have built

        Your job is to:
        A. Describe what the rules actually do in plain English.
        B. Identify gaps between intention and rules:
           - "missing": something the user wanted but is not in the rules
           - "wrong": something in the rules that contradicts the intention
           - "extra": something in the rules the user did NOT mention
        C. Give an overall result:
           - "match": rules fully satisfy the intention
           - "partial": rules mostly match but something is missing or slightly off
           - "mismatch": rules significantly differ from the intention

        Return ONLY valid JSON, no markdown, no preamble:
        {
          "result": "match|partial|mismatch",
          "whatItDoes": "Plain English of what the rules actually do.",
          "whatYouWanted": "Plain English restatement of the user intent.",
          "gaps": [
            { "type": "missing|wrong|extra", "description": "Specific gap." }
          ]
        }
        """;

            var userPrompt =
                $"USER'S INTENTION: \"{request.Intent}\"\n\n" +
                $"ACTUAL RULES BUILT:\n{readableRules}\n\n" +
                $"Analyse and return the JSON result.";

            var (responseJson, tokens1) = await CallGroqAsync(
                systemPrompt, userPrompt, maxTokens: 800);

            IntentCheckResponseDto result;

            try
            {
                var cleaned = CleanJson(responseJson);
                var parsed = JsonSerializer.Deserialize<JsonObject>(cleaned, _jsonOptions);

                var overallResult = parsed?["result"]?.GetValue<string>() ?? "partial";
                var whatItDoes = parsed?["whatItDoes"]?.GetValue<string>() ?? "";
                var whatYouWanted = parsed?["whatYouWanted"]?.GetValue<string>() ?? request.Intent;

                var gaps = new List<IntentGap>();
                var gapsArray = parsed?["gaps"]?.AsArray();
                if (gapsArray != null)
                {
                    foreach (var item in gapsArray)
                    {
                        if (item == null) continue;
                        gaps.Add(new IntentGap
                        {
                            Type = item["type"]?.GetValue<string>() ?? "missing",
                            Description = item["description"]?.GetValue<string>() ?? ""
                        });
                    }
                }

                result = new IntentCheckResponseDto
                {
                    Result = overallResult,
                    WhatItDoes = whatItDoes,
                    WhatYouWanted = whatYouWanted,
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

            // ── Step 2: if not a perfect match, generate corrected rule tree ─
            if (result.Result != "match")
            {
                try
                {
                    var fixSystemPrompt = BuildSelectionSystemPrompt() + """


                ADDITIONAL RULES:
                Build the corrected rule tree that fully satisfies the user's intention.
                Use ONLY IDs from the provided catalog.
                """;

                    var fixUserPrompt = BuildSelectionUserPrompt(
                        $"Build a rule tree that exactly matches this intention: \"{request.Intent}\"",
                        catalog);

                    var (fixJson, tokens2) = await CallGroqAsync(
                        fixSystemPrompt, fixUserPrompt, maxTokens: 2000,
                        model: _options.PowerModel);

                    var (fixedGroup, _, fixParseSuccess) = ParseSelectionJson(fixJson, catalog);

                    if (fixParseSuccess && CountRules(fixedGroup) > 0)
                    {
                        var enriched = await EnrichGroupAsync(fixedGroup);
                        var enrichedPrompt = SelectionPromptBuilder.BuildUserPrompt(enriched);
                        var (fixDesc, tokens3) = await CallGroqAsync(
                            DescriptionSystemPrompt, enrichedPrompt);

                        result.SuggestedFix = fixedGroup;
                        result.SuggestedFixDescription = fixDesc;
                        result.TokensUsed += tokens2 + tokens3;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not generate suggested fix");
                }
            }

            return result;
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════

        private static HashSet<int> CollectNodeIds(SelectionGroupDto group)
        {
            var ids = new HashSet<int>();
            if (group.Rules != null)
                foreach (var r in group.Rules)
                    ids.Add(r.TreeNodeId);
            if (group.Groups != null)
                foreach (var child in group.Groups)
                    ids.UnionWith(CollectNodeIds(child));
            return ids;
        }

        private static string BuildCacheKey(string prefix, object obj)
        {
            var json = JsonSerializer.Serialize(obj);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
            return $"{prefix}{Convert.ToHexString(hash)[..16]}";
        }

        /// Strips markdown code fences and repairs truncated/malformed JSON
        private static string CleanJson(string raw)
        {
            var s = raw.Trim();
            if (s.StartsWith("```json")) s = s[7..];
            else if (s.StartsWith("```")) s = s[3..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim().TrimEnd(',');

            var openBraces = s.Count(c => c == '{') - s.Count(c => c == '}');
            var openBrackets = s.Count(c => c == '[') - s.Count(c => c == ']');

            if (openBrackets < 0)
                for (int i = 0; i < -openBrackets; i++)
                {
                    var idx = s.LastIndexOf(']');
                    if (idx >= 0) s = s.Remove(idx, 1);
                }
            else
                for (int i = 0; i < openBrackets; i++) s += "]";

            if (openBraces < 0)
                for (int i = 0; i < -openBraces; i++)
                {
                    var idx = s.LastIndexOf('}');
                    if (idx >= 0) s = s.Remove(idx, 1);
                }
            else
                for (int i = 0; i < openBraces; i++) s += "}";

            return s;
        }
    }

    // ── Catalog item: lightweight node representation ─────────────────────────
    public sealed class NodeCatalogItem
    {
        public int Id { get; set; }
        public string NodeName { get; set; } = "";
        public string NodeDesc { get; set; } = "";
        public string DataType { get; set; } = "";
        public string Category { get; set; } = "";
        public string SearchText { get; set; } = "";
    }
}