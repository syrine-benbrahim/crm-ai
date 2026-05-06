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
        private readonly IAiUsageService _usageService;

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
            IMemoryCache cache,
            IAiUsageService usageService)
        {
            _httpClient = httpClientFactory.CreateClient("GrokClient");
            _options = options.Value;
            _context = context;
            _logger = logger;
            _cache = cache;
            _usageService = usageService;
        }

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
        private async Task<Dictionary<int, NodeCatalogItem>> BuildNodeCatalogAsync()
        {
            var fingerprint = await _context.TreeNodes
                .Where(n => n.IsSelectable == 1)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), MaxId = g.Max(n => n.Id) })
                .FirstOrDefaultAsync();

            var cacheKey = $"node_catalog_{fingerprint?.Count}_{fingerprint?.MaxId}";

            if (_cache.TryGetValue(cacheKey, out Dictionary<int, NodeCatalogItem>? cached) && cached != null)
                return cached;

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
                ParentId = n.ParentId ?? 0,
                ParentName = n.ParentName ?? "",
                NodeName = n.AiLabel ?? n.NodeName ?? "",
                NodeDesc = n.AiLabel ?? n.NodeDesc ?? n.NodeName ?? "",
                DataType = n.DataType ?? "",
                Category = n.SemanticCategory ?? n.ParentName ?? n.EntityName ?? "General",
                SearchText = $"{n.AiLabel} {n.NodeName} {n.NodeDesc} {n.SemanticCategory} {n.ParentName} {n.DataType}".ToLower()
            });

            _cache.Set(cacheKey, catalog, TimeSpan.FromHours(1));
            _logger.LogInformation("Built node catalog with {Count} selectable nodes (key={Key})",
                catalog.Count, cacheKey);
            return catalog;
        }
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
        private string BuildSelectionUserPrompt(
    string userRequest, Dictionary<int, NodeCatalogItem> catalog)
        {
            const int MaxNodesUnfiltered = 120;
            const int MaxNodesFiltered = 200;

            var nodes = catalog.Values.ToList();
            int effectiveMax = nodes.Count <= MaxNodesFiltered ? nodes.Count : MaxNodesUnfiltered;

            if (nodes.Count > effectiveMax)
            {
                var categoryCount = nodes.Select(n => n.Category).Distinct().Count();
                nodes = nodes
                    .GroupBy(n => n.Category).OrderBy(g => g.Key)
                    .SelectMany(g => g.Take(Math.Max(1, (int)Math.Ceiling((double)effectiveMax / categoryCount))))
                    .Take(effectiveMax).ToList();
            }

            var catalogCacheKey = "catalog_str_" +
                string.Join(",", nodes.Select(n => n.Id).OrderBy(x => x));

            if (!_cache.TryGetValue(catalogCacheKey, out string? catalogStr))
            {
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
        private async Task<List<ClarificationBlockDto>?> BuildClarificationPayloadAsync(
           string userMessage,
           Dictionary<int, NodeCatalogItem> catalog)
        {
            var words = userMessage
                .ToLower()
                .Split(new[] { ' ', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !ScoringStopWords.Contains(w))
                .Distinct()
                .ToArray();

            if (words.Length == 0) return null;

            var categoryKeywords = CatalogKeywords.Build(catalog);

            var scoredParents = catalog.Values
                .Where(n => n.ParentId > 0)
                .GroupBy(n => n.ParentId)
                .Select(g =>
                {
                    var semanticCategory = g.First().Category;

                    var searchScore = g.Sum(n =>
                        words.Count(w =>
                        {
                            var text = n.SearchText;
                            var idx = text.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                            while (idx >= 0)
                            {
                                var before = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                                var after = idx + w.Length >= text.Length
                                            || !char.IsLetterOrDigit(text[idx + w.Length]);
                                if (before && after) return true;
                                idx = text.IndexOf(w, idx + 1, StringComparison.OrdinalIgnoreCase);
                            }
                            return false;
                        }));

                    var categoryScore = categoryKeywords.TryGetValue(semanticCategory, out var keywords)
                        ? words.Count(w => keywords.Contains(w))
                        : 0;

                    return new
                    {
                        ParentId = g.Key,
                        ParentName = g.First().ParentName,
                        DataType = g.First().DataType,
                        SemanticCategory = semanticCategory,
                        Nodes = g.OrderBy(n => n.Id).ToList(),
                        Score = searchScore + categoryScore,
                        IsVague = searchScore == 0
                    };
                })
                .Where(g => g.Score > 0)
                .GroupBy(g => g.SemanticCategory)
                .SelectMany(g =>
                {
                    var ordered = g.OrderByDescending(x => x.Score).ToList();
                    if (ordered.Count >= 2 &&
                        !string.Equals(ordered[0].ParentName, ordered[1].ParentName,
                            StringComparison.OrdinalIgnoreCase))
                        return ordered.Take(2);
                    return ordered.Take(1);
                })
                .OrderByDescending(g => g.Score)
                .Take(4)
                .ToList();

            _logger.LogInformation(
                "Clarification scoring for \"{Msg}\" | {Count} groups | Top: [{Top}]",
                userMessage,
                scoredParents.Count,
                string.Join(", ", scoredParents.Take(4)
                    .Select(g => $"{g.ParentName}={g.Score}(vague={g.IsVague})")));

            var scoredParentKeys = new HashSet<int>(scoredParents.Select(g => g.ParentId));
            scoredParents = scoredParents
                .Where(g => !g.Nodes.Any(n => scoredParentKeys.Contains(n.Id)))
                .ToList();

            if (scoredParents.Count == 0) return null;

            var wordToParents = new Dictionary<string, HashSet<int>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var group in scoredParents)
            {
                foreach (var word in words)
                {
                    bool matchesGroup = group.Nodes.Any(n =>
                    {
                        var text = n.SearchText;
                        var idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                        while (idx >= 0)
                        {
                            var before = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                            var after = idx + word.Length >= text.Length
                                        || !char.IsLetterOrDigit(text[idx + word.Length]);
                            if (before && after) return true;
                            idx = text.IndexOf(word, idx + 1, StringComparison.OrdinalIgnoreCase);
                        }
                        return false;
                    });

                    if (!matchesGroup)
                        matchesGroup = categoryKeywords.TryGetValue(
                            group.SemanticCategory, out var kws) && kws.Contains(word);

                    if (matchesGroup)
                    {
                        if (!wordToParents.ContainsKey(word))
                            wordToParents[word] = new HashSet<int>();
                        wordToParents[word].Add(group.ParentId);
                    }
                }
            }

            var ambiguousParentIds = wordToParents
                .Where(kv => kv.Value.Count > 1)
                .SelectMany(kv => kv.Value)
                .ToHashSet();

            _logger.LogInformation(
                "Ambiguity check: {Count} ambiguous groups — [{Groups}]",
                ambiguousParentIds.Count,
                string.Join(", ", scoredParents
                    .Where(g => ambiguousParentIds.Contains(g.ParentId))
                    .Select(g => g.ParentName)));

            // ── DIRECT PHRASE MATCH ───────────────────────────────────────────────
            // If the user typed a group's name as a phrase (e.g. "north west"),
            // remove all other groups that only scored on partial shared words.
            var directlyMatchedParentIds = new HashSet<int>();

            foreach (var group in scoredParents)
            {
                var groupName = (group.ParentName ?? "").ToLower().Trim();
                if (!string.IsNullOrWhiteSpace(groupName) &&
                    userMessage.ToLower().Contains(groupName))
                {
                    directlyMatchedParentIds.Add(group.ParentId);
                }
            }

            if (directlyMatchedParentIds.Count > 0)
            {
                var before = scoredParents.Count;
                scoredParents = scoredParents
                    .Where(g => directlyMatchedParentIds.Contains(g.ParentId))
                    .ToList();

                _logger.LogInformation(
                    "Direct phrase match: kept {After}/{Before} groups — [{Kept}]",
                    scoredParents.Count, before,
                    string.Join(", ", scoredParents.Select(g => g.ParentName)));

                if (scoredParents.Count <= 1) return null;

                // Rebuild ambiguousParentIds after filtering
                ambiguousParentIds = scoredParents.Select(g => g.ParentId).ToHashSet();
            }
            // ─────────────────────────────────────────────────────────────────────

            // No word is ambiguous — let the AI build directly, no cards needed
            var allAmbiguousAreVague = scoredParents
                .Where(g => ambiguousParentIds.Contains(g.ParentId))
                .All(g => g.IsVague);

            if (allAmbiguousAreVague)
            {
                _logger.LogInformation(
                    "All ambiguous groups are vague — skipping clarification, letting AI build");
                return null;
            }
            if (ambiguousParentIds.Count == 0) return null;

            var blocks = new List<ClarificationBlockDto>();

            foreach (var group in scoredParents
                .Where(g => ambiguousParentIds.Contains(g.ParentId))
                .Take(3))
            {
                var options = group.Nodes
                    .Take(12)
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

            if (blocks.Count == 0) return null;
            blocks = await RewriteClarificationLabelsAsync(userMessage, blocks);
            return blocks;
        }
        private async Task<List<ClarificationBlockDto>> RewriteClarificationLabelsAsync(
    string userPrompt,
    List<ClarificationBlockDto> blocks)
        {
            const string system =
                """
        You rewrite CRM filter option labels into friendly, plain-English UI text.
        The user typed a prompt. You are shown ONE group of filter options.

        - Rewrite "label" as a short, clear question (max 8 words)
        - Rewrite each option's "label" as a natural phrase a non-technical user would understand
        - Keep "id", "type", "optionId", "rules", "isFallback" exactly as-is
        - Never add or remove options
        - Return ONLY valid JSON — the same single block object, rewritten

        Examples:
          block label "Total transaction value" → "What total spend are you targeting?"
          block label "Average transaction value" → "What average spend are you targeting?"
          block label "Segment" → "Which loyalty tier?"
          block label "Recency" → "How recently did they visit?"
          option label "<= 7 days" → "Within the last week"
          option label "Loyal" → "Loyal customers"
          option label "Lapsed" → "Lapsed customers"
        """;

            var result = new List<ClarificationBlockDto>();

            foreach (var block in blocks)
            {
                try
                {
                    var blockJson = JsonSerializer.Serialize(block, _camelCaseOptions);
                    var userPromptText =
                        $"User typed: \"{userPrompt}\"\n\nBlock to rewrite:\n{blockJson}";

                    // Token budget for ONE block only — much smaller, never truncates
                    int estimatedTokens = block.Options.Count * 40 + 200;
                    estimatedTokens = Math.Clamp(estimatedTokens, 400, 800);

                    var (responseJson, _) = await CallGroqAsync(
                        system, userPromptText,
                        maxTokens: estimatedTokens, skipDelay: true);

                    var cleaned = CleanJson(responseJson);

                    var rewritten = JsonSerializer.Deserialize<ClarificationBlockDto>(
                        cleaned, _jsonOptions);

                    if (rewritten != null && rewritten.Options?.Count == block.Options.Count)
                    {
                        result.Add(rewritten);
                        _logger.LogInformation(
                            "Label rewrite succeeded for block: {BlockId}", block.Id);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Label rewrite wrong shape for block {BlockId} — using raw",
                            block.Id);
                        result.Add(block);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Label rewrite failed for block {BlockId} — using raw", block.Id);
                    result.Add(block);
                }
            }

            return result;
        }

        private (SelectionGroupDto Group, List<string> UnmatchedTerms, bool Success)
            ParseSelectionJson(string rawJson, Dictionary<int, NodeCatalogItem> catalog)
        {
            try
            {
                var cleaned = CleanJson(rawJson);
                var parsed = JsonSerializer.Deserialize<JsonObject>(cleaned, _jsonOptions);
                if (parsed == null) return (EmptyGroup(), new(), false);
                SelectionGroupDto? rootGroup = null;

                var rootGroupNode = parsed["rootGroup"] ?? parsed["RootGroup"];
                if (rootGroupNode != null)
                {
                    rootGroup = JsonSerializer.Deserialize<SelectionGroupDto>(
                        rootGroupNode.ToJsonString(), _jsonOptions);
                }
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

        private void ValidateNodeIds(SelectionGroupDto group,
            Dictionary<int, NodeCatalogItem> catalog, List<string> unmatched)
        {
            if (group.Rules != null)
            {
                group.Rules = group.Rules
                    .Where(rule =>
                    {
                        if (rule.TreeNodeId <= 0)
                        {
                            _logger.LogWarning(
                                "Removing rule with invalid TreeNodeId: {Id}",
                                rule.TreeNodeId);
                            unmatched.Add($"Could not find a filter for one of your conditions");
                            return false;
                        }
                        if (!catalog.ContainsKey(rule.TreeNodeId))
                        {
                            unmatched.Add($"Unknown node ID: {rule.TreeNodeId}");
                            _logger.LogWarning(
                                "AI used invalid TreeNodeId: {Id}", rule.TreeNodeId);
                            return false;
                        }
                        rule.Operator ??= "=";
                        rule.Value ??= "";
                        return true;
                    })
                    .ToList();
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

                    if (!isFirstBuild)
                    {
                        var intentMsg = FindEffectiveUserRequest(request.Messages);
                        var intentCatalog = await BuildNodeCatalogAsync();
                        var intentClarifications = await BuildClarificationPayloadAsync(intentMsg, intentCatalog);

                        if (intentClarifications != null && intentClarifications.Count > 0)
                        {
                            var stateId = SaveClarificationState(intentClarifications);
                            _logger.LogInformation(
                                "Clarification intercepted before refine build for: \"{Msg}\"", intentMsg);

                            return new ConversationResponseDto
                            {
                                Status = "clarifying_structured",
                                Message = $"Before I apply that, I need a bit more detail:",
                                Questions = new(),
                                Clarifications = intentClarifications,
                                ClarificationStateId = stateId,
                                Selection = null,
                                TokensUsed = 0
                            };
                        }
                    }

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
                    bool hasClarificationAnswers =
                        !string.IsNullOrWhiteSpace(request.ClarificationStateId) &&
                        request.ClarificationAnswers != null &&
                        request.ClarificationAnswers.Count > 0;

                    if (!hasClarificationAnswers)  
                    {
                        var lastMsg = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                        var refineCheckCatalog = await BuildNodeCatalogAsync();

                        var clarifications = await BuildClarificationPayloadAsync(lastMsg, refineCheckCatalog);
                        if (clarifications != null && clarifications.Count > 0)
                        {
                            var stateId = SaveClarificationState(clarifications);
                            _logger.LogInformation("Clarification triggered mid-refine for: \"{Msg}\"", lastMsg);
                            return new ConversationResponseDto
                            {
                                Status = "clarifying_structured",
                                Message = $"I found a few possible meanings for '{lastMsg}'. Pick the ones that apply:",
                                Questions = new(),
                                Clarifications = clarifications,
                                ClarificationStateId = stateId,
                                Selection = null,
                                TokensUsed = 0
                            };
                        }

                        _logger.LogInformation("Refine turn — showing intent confirmation");
                        var currentRulesDesc = DescribeGroup(request.CurrentRootGroup!, refineCheckCatalog);
                        return await HandleIntentConfirmationAsync(
                            request.Messages, existingRulesContext, 0,
                            isRefine: true, currentRulesDesc: currentRulesDesc);
                    }
                }
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

                    var builtGroup = SelectionGroupBuilder.BuildGroupFromResolvedAnswers(clarificationAnswerDtos);
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
            var clarifications = await BuildClarificationPayloadAsync(lastUserMessage, catalog);

            if (clarifications != null && clarifications.Count > 0)
            {
                var stateId = SaveClarificationState(clarifications);   

                _logger.LogInformation(
                    "Structured clarification returned {Count} blocks for: \"{Msg}\"",
                    clarifications.Count, lastUserMessage);

                return new ConversationResponseDto
                {
                    Status = "clarifying_structured",
                    Message = $"I found a few possible meanings for '{lastUserMessage}'. Pick the ones that apply:",
                    Questions = new(),
                    Clarifications = clarifications,
                    ClarificationStateId = stateId,                   
                    Selection = null,
                    TokensUsed = previousTokens
                };
            }
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
        private static string BuildIdHints(Dictionary<int, NodeCatalogItem> catalog)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CRITICAL ID MAPPINGS (from live catalog — use these exact IDs):");

            var hintCategories = new[]
            {
        "Recency", "Visits", "Loyalty", "Spend",
        "Age", "Gender", "Location", "Dwell Time",
        "Visit Pattern", "Contact", "Site",
        "Email Engagement", "SMS Engagement", "Profile"
    };

            foreach (var cat in hintCategories)
            {
                var nodes = catalog.Values
                    .Where(n => n.Category == cat)
                    .OrderBy(n => n.Id)
                    .Take(12)
                    .ToList();

                if (nodes.Count == 0) continue;

                sb.Append($"{cat}: ");
                sb.AppendLine(string.Join(", ", nodes.Select(n => $"'{n.NodeName}'={n.Id}")));
            }

            return sb.ToString();
        }
        private async Task<(SelectionGroupDto? Group, int Tokens)> BuildNewGroupAsync(
            string userMessage, Dictionary<int, NodeCatalogItem> catalog)
        {
            var idHints = BuildIdHints(catalog);

            var newGroupSystem =
                $$"""
            You are a CRM rule group builder.
            Generate a SINGLE rule group JSON object for the described audience.
            Use ONLY the IDs listed below — do not invent IDs.
            logicalOperator must be "AND", "OR", or "EXCLUDE".
            operator is always "=" and value is always "".
            Return ONLY valid JSON — no markdown, no preamble, no rootGroup wrapper.

            {{idHints}}

            RULES:
            - Return ONE flat AND group containing ALL conditions in its "rules" array
            - Only create a sub-group when you genuinely need OR logic
              (e.g. multiple age ranges, multiple cities for the SAME condition)
            - NEVER wrap the result in an outer OR or AND shell — return the group directly
            - Date/recency: use the most specific matching recency ID
            - Age ranges: OR sub-group with each matching age ID
            - "over £X" spend: OR sub-group of ALL spend IDs above X

            OUTPUT FORMAT — return ONLY this shape:
            {"logicalOperator":"AND","rules":[{"treeNodeId":123,"operator":"=","value":""},{"treeNodeId":456,"operator":"=","value":""}],"groups":[]}

            NEVER wrap in a rootGroup or any outer group. Return only the single group object.
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

                JsonNode? groupNode = parsed?["rootGroup"] ?? parsed?["RootGroup"];
                string groupJson = groupNode != null ? groupNode.ToJsonString() : cleaned;

                var raw = JsonSerializer.Deserialize<SelectionGroupDto>(groupJson, _jsonOptions);
                var group = UnwrapToMeaningfulGroup(raw);

                _logger.LogInformation(
                    "BuildNewGroupAsync parsed: operator={Op}, rules={RuleCount}, groups={GroupCount}",
                    group?.LogicalOperator, group?.Rules?.Count ?? 0, group?.Groups?.Count ?? 0);

                if (group != null) NormalizeGroup(group);
                return (group, tokens);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BuildNewGroupAsync failed for message: {Msg}", userMessage);
                return (null, 0);
            }
        }
        private async Task<ConversationResponseDto> FinaliseSelectionResponseAsync(
    SelectionGroupDto rootGroup,
    List<string> unmatchedTerms,
    string? name,
    int previousTokens,
    bool isRefine)
        {
            NormalizeGroup(rootGroup);
            var catalog = await BuildNodeCatalogAsync();
            rootGroup = SelectionSanitizer.Sanitize(rootGroup, catalog, _logger);
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

        private async Task<(string Response, int TokensUsed)> CallGroqAsync(
            string systemPrompt,
            string userPrompt,
            int maxTokens = 300,
            bool skipDelay = false,
            string? model = null)
        {
            var effectiveModel = model ?? _options.Model;

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
            new() { Role = "user",   Content = userPrompt }
        }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await _httpClient.PostAsync(
                    "https://api.groq.com/openai/v1/chat/completions", content);
            }
            catch (Polly.CircuitBreaker.BrokenCircuitException ex)
            {
                _logger.LogWarning("Groq circuit open — failing fast");
                throw new InvalidOperationException(
                    "The AI service is temporarily unavailable. " +
                    "Please use the visual builder or try again in 30 seconds.", ex);
            }
            catch (Polly.Timeout.TimeoutRejectedException ex)
            {
                _logger.LogWarning("Groq request timed out");
                throw new InvalidOperationException(
                    "The AI service took too long to respond. Please try again.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Groq HTTP call failed");
                _usageService.Record(
                    model: effectiveModel,
                    feature: "groq_call",
                    tokensUsed: 0,
                    maxTokens: maxTokens,
                    success: false,
                    errorMessage: ex.Message);
                throw new InvalidOperationException(
                    "AI service is currently unavailable.", ex);
            }

            if ((int)httpResponse.StatusCode == 413 || (int)httpResponse.StatusCode == 404)
            {
                if (effectiveModel != _options.Model)
                {
                    _logger.LogWarning(
                        "HTTP {Status} on '{Model}' — falling back to '{Fast}'",
                        httpResponse.StatusCode, effectiveModel, _options.Model);
                    return await CallGroqAsync(
                        systemPrompt, userPrompt, maxTokens, skipDelay, _options.Model);
                }
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync();
                _logger.LogError("Groq API error {Status}: {Body}",
                    httpResponse.StatusCode, errorBody);
                throw new InvalidOperationException(
                    $"AI service returned {httpResponse.StatusCode}.");
            }

            var responseJson = await httpResponse.Content.ReadAsStringAsync();
            var grokResponse = JsonSerializer.Deserialize<GrokResponse>(responseJson);

            var firstChoice = grokResponse?.Choices?.FirstOrDefault();
            var responseText = firstChoice?.Message?.Content?.Trim() ?? "";
            var tokensUsed = grokResponse?.Usage?.TotalTokens ?? 0;

            if (firstChoice?.FinishReason == "length" && effectiveModel != _options.Model)
            {
                _logger.LogWarning(
                    "finish_reason=length on '{Model}' — retrying with '{Fast}'",
                    effectiveModel, _options.Model);
                return await CallGroqAsync(
                    systemPrompt, userPrompt, maxTokens, skipDelay, _options.Model);
            }

            _logger.LogInformation(
                "Groq call complete ({Tokens} tokens, model={Model})",
                tokensUsed, effectiveModel);
            _logger.LogInformation(
                "BILLING | Model={Model} | Tokens={Tokens} | MaxTokens={MaxTokens}",
                effectiveModel, tokensUsed, maxTokens);
            _usageService.Record(
                model: effectiveModel,
                feature: "groq_call",
                tokensUsed: tokensUsed,
                maxTokens: maxTokens,
                success: true);

            return (responseText, tokensUsed);
        }
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
        // CONFIDENCE SCORING
        internal static (int Score, string[] Reasons) ComputeRuleBasedConfidence(
            SelectionGroupDto rootGroup,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            var reasons = new List<string>();
            int score = 0;

            int totalRules = CountRules(rootGroup);
            int totalGroups = CountGroups(rootGroup);

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
            if (totalGroups >= 2)
            {
                score += 20; 
            }
            else if (totalGroups == 1)
            {
                score += 10;
            }
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
            bool hasEmptyGroups = HasEmptyGroups(rootGroup);
            if (!hasEmptyGroups)
                score += 15;
            else
                reasons.Add("Some groups have no rules");

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

        public async Task<(string Response, int TokensUsed)> CallPublicAsync(
    string systemPrompt, string userPrompt, int maxTokens = 200,
    string? model = null)
        {
            return await CallGroqAsync(
                systemPrompt, userPrompt, maxTokens,
                skipDelay: true, model: model);
        }

        public string FastModel => _options.Model;

        public string PowerModel =>
            string.IsNullOrWhiteSpace(_options.PowerModel)
                ? _options.Model
                : _options.PowerModel;
        public async Task<Dictionary<int, NodeCatalogItem>> BuildNodeCatalogPublicAsync()
        {
            return await BuildNodeCatalogAsync();
        }

        private static HashSet<int> CollectNodeIds(SelectionGroupDto group)
        {
            var ids = new HashSet<int>();
            if (group.Rules != null) foreach (var r in group.Rules) ids.Add(r.TreeNodeId);
            if (group.Groups != null) foreach (var child in group.Groups) ids.UnionWith(CollectNodeIds(child));
            return ids;
        }
        private static string FindEffectiveUserRequest(List<ConversationMessage> messages)
        {
            var confirmationPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "yes build it", "yes, build it", "build it", "confirm",
        "ok", "okay", "do it", "go ahead", "yes please", "sure", "proceed",
        "apply it", "apply", "yes apply", "yes, apply"
    };
            var userMessages = messages
                .Where(m => m.Role == "user")
                .ToList();

            for (int i = userMessages.Count - 1; i >= 0; i--)
            {
                var content = userMessages[i].Content?.Trim() ?? "";
                if (!confirmationPhrases.Contains(content))
                    return content;
            }
            return messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        }
        // CLARIFICATION STATE HELPERS
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
        private static readonly HashSet<string> ScoringStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            // Generic domain words
            "customers", "customer", "people", "person", "users", "user",
            "audience", "members", "member", "visitors", "visitor",
            // Common English function words
            "who", "that", "which", "the", "and", "but", "for", "with",
            "has", "have", "are", "were", "they", "their", "them",
            "all", "any", "some", "can", "not", "get", "want", "need",
            "find", "show", "give", "our", "been", "this", "from",
            // ← ADD THESE: comparison words that appear in node names
            "than", "more", "less", "over", "under", "above", "below",
            "older", "younger", "bigger", "smaller", "higher", "lower",
            "regularly", "regular", "typical", "usually", "usual", "often", "frequent",
            "first", "second", "third", "fourth", "last",
            "one", "two", "three", "four", "five",
            "group", "groups", "rule", "rules", "filter", "filters",
            "condition", "conditions"
        };
        private static readonly HashSet<string> MultiSelectDataTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "agerange", "cityregion", "string", "dayofweek", "hourofday", "notnull", "siteid"
            };
        private static SelectionGroupDto UnwrapToMeaningfulGroup(SelectionGroupDto? group)
        {
            if (group == null) return EmptyGroup();
            if (group.Rules?.Count > 0) return group;
            if ((group.Rules == null || group.Rules.Count == 0)
                && group.Groups?.Count == 1)
            {
                return UnwrapToMeaningfulGroup(group.Groups[0]);
            }
            return group;
        }
        // ════════════════════════════════════════════════════════════════════
        // SEMANTIC CATEGORY KEYWORDS — bridges natural language to categories
        // Only needs updating if you add a new SemanticCategory to the tree
        // ════════════════════════════════════════════════════════════════════

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

        private string CleanJson(string raw) => JsonRepair.Clean(raw, _logger);

        // ON-DEMAND: DESCRIPTION

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
        // ON-DEMAND: NAME
        public async Task<string> GenerateNameOnDemandAsync(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return "Untitled Selection";

            var (name, _) = await CallGroqAsync(
                PromptTemplates.Description.NameSystem,
                PromptTemplates.Description.NameUser(description),
                maxTokens: 30, skipDelay: true);

            return name.Trim('"', '.', ' ');
        }

        // ON-DEMAND: CONFIDENCE

        public async Task<(int Score, int Tokens)> ScoreConfidenceOnDemandAsync(
            string intent, SelectionGroupDto rootGroup)
        {
            var catalog = await BuildNodeCatalogAsync();
            return await ScoreConfidenceAsync(intent, rootGroup, catalog);
        }
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