using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Services;
using Microsoft.EntityFrameworkCore;

namespace crm_ai.Helpers
{
    public class SegmentProfileBuilder
    {
        private readonly AppDbContext _context;
        private readonly ILogger<SegmentProfileBuilder> _logger;

        public SegmentProfileBuilder(
            AppDbContext context,
            ILogger<SegmentProfileBuilder> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<SegmentProfileDto> BuildAsync(
            SelectionGroupDto rootGroup,
            Dictionary<int, NodeCatalogItem> catalog,
            string selectionDescription)
        {
            // ── Step 1: Read what the rule tree says ─────────────────────
            // We extract node names to understand WHAT was selected
            // (recency ranges, loyalty tiers, spend brackets, gender, location)
            var nodeNames = CollectNodeNames(rootGroup, catalog);

            _logger.LogInformation(
                "SegmentProfileBuilder: {Count} nodes in tree: [{Nodes}]",
                nodeNames.Count,
                string.Join(", ", nodeNames.Take(10)));

            // ── Step 2: Classify from rule tree (C# lookup) ───────────────
            var engagementLevel = ClassifyEngagement(nodeNames);
            var valueTier = ClassifyValue(nodeNames);
            var dominantRecency = GetDominantRecency(nodeNames);
            var dominantLoyalty = GetDominantLoyalty(nodeNames);
            var dominantSpend = GetDominantSpend(nodeNames);
            var gender = GetGender(nodeNames);
            var ageRanges = GetAgeRanges(nodeNames);
            var locations = GetLocations(nodeNames, catalog);

            // ── Step 3: Compute real metrics from DB ──────────────────────
            // These are aggregate queries — fast, no customer list needed
            var totalCustomers = await _context.Customers.CountAsync();
            var emailCount = await _context.Customers
                .CountAsync(c => c.Email != null && c.Email != "");
            var phoneCount = await _context.Customers
                .CountAsync(c => c.Phone != null && c.Phone != "");

            // Email and SMS coverage as percentage
            float emailCoverage = totalCustomers > 0
                ? (float)emailCount / totalCustomers : 0f;
            float smsCoverage = totalCustomers > 0
                ? (float)phoneCount / totalCustomers : 0f;

            // ── Step 4: Visit patterns for send time ──────────────────────
            // Top 5 day+hour combinations by visit frequency
            var visitPatterns = await GetVisitPatternsAsync();

            string? recommendedDay = visitPatterns.FirstOrDefault()?.Day;
            string? recommendedHour = visitPatterns.FirstOrDefault()?.Hour;

            // ── Step 5: Build the behaviour summary sentence ──────────────
            // This is what the AI reads first — one clear English sentence
            // built from structured data, not from the AI itself
            var summary = BuildSummary(
                totalCustomers, engagementLevel, valueTier,
                dominantRecency, dominantLoyalty, dominantSpend,
                locations, ageRanges, gender,
                emailCoverage, smsCoverage);

            _logger.LogInformation(
                "Segment profile: engagement={Eng}, value={Val}, email={Email:P0}",
                engagementLevel, valueTier, emailCoverage);

            return new SegmentProfileDto
            {
                AudienceSize = totalCustomers,
                EmailCoveragePercent = MathF.Round(emailCoverage * 100f, 1),
                SmsCoveragePercent = MathF.Round(smsCoverage * 100f, 1),
                DominantRecency = dominantRecency,
                DominantLoyaltyTier = dominantLoyalty,
                DominantSpendTier = dominantSpend,
                DominantLocations = locations,
                AgeRanges = ageRanges,
                Gender = gender,
                EngagementLevel = engagementLevel,
                ValueTier = valueTier,
                BehaviourSummary = summary,
                SelectionDescription = selectionDescription,
                RecommendedSendDay = recommendedDay,
                RecommendedSendHour = recommendedHour,
                TopVisitPatterns = visitPatterns
            };
        }

        // ════════════════════════════════════════════════════════════════
        // CLASSIFICATION — pure C# lookups, no AI
        // ════════════════════════════════════════════════════════════════

        // Recency node names → engagement level
        // Order matters: most lapsed wins (worst case drives strategy)
        private static readonly Dictionary<string, string> RecencyToEngagement =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Yesterday"] = "Active",
                ["<= 7 days"] = "Active",
                ["8-14 days"] = "Active",
                ["15-31 days"] = "Active",
                ["1-2 months"] = "AtRisk",
                ["2-3 months"] = "AtRisk",
                ["3-4 Months"] = "Lapsed",
                ["4 months +"] = "LongTermLapsed",
            };

        private static readonly Dictionary<string, string> SpendToTier =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["£50-£60"] = "High",
                ["£60-£70"] = "High",
                ["£70-£80"] = "High",
                ["£80-£90"] = "High",
                ["£90-£100"] = "High",
                ["£100+"] = "High",
                ["£30-£40"] = "Medium",
                ["£40-£50"] = "Medium",
                ["£10-£20"] = "Low",
                ["£20-£30"] = "Low",
            };

        private static readonly string[] LoyaltyNodes =
            ["Loyal", "Frequent", "Occasional", "Infrequent", "Lapsed", "Long-term lapsed", "Never"];

        private static string ClassifyEngagement(HashSet<string> nodeNames)
        {
            // Priority: LongTermLapsed > Lapsed > AtRisk > Active
            var engagements = nodeNames
                .Where(n => RecencyToEngagement.ContainsKey(n))
                .Select(n => RecencyToEngagement[n])
                .ToList();

            if (engagements.Contains("LongTermLapsed")) return "LongTermLapsed";
            if (engagements.Contains("Lapsed")) return "Lapsed";
            if (engagements.Contains("AtRisk")) return "AtRisk";
            if (engagements.Contains("Active")) return "Active";

            // Loyalty tier overrides if no recency node present
            if (nodeNames.Contains("Lapsed") || nodeNames.Contains("Long-term lapsed"))
                return "LongTermLapsed";

            return "Unknown";
        }

        private static string ClassifyValue(HashSet<string> nodeNames)
        {
            var tiers = nodeNames
                .Where(n => SpendToTier.ContainsKey(n))
                .Select(n => SpendToTier[n])
                .ToList();

            if (tiers.Contains("High")) return "High";
            if (tiers.Contains("Medium")) return "Medium";
            if (tiers.Count > 0) return "Low";
            return "Unknown";
        }

        private static string GetDominantRecency(HashSet<string> nodeNames) =>
            nodeNames.FirstOrDefault(n => RecencyToEngagement.ContainsKey(n)) ?? "";

        private static string GetDominantLoyalty(HashSet<string> nodeNames) =>
            nodeNames.FirstOrDefault(n =>
                LoyaltyNodes.Contains(n, StringComparer.OrdinalIgnoreCase)) ?? "";

        private static string GetDominantSpend(HashSet<string> nodeNames) =>
            nodeNames.FirstOrDefault(n => SpendToTier.ContainsKey(n)) ?? "";

        private static string? GetGender(HashSet<string> nodeNames)
        {
            if (nodeNames.Contains("Female", StringComparer.OrdinalIgnoreCase)) return "Female";
            if (nodeNames.Contains("Male", StringComparer.OrdinalIgnoreCase)) return "Male";
            return null;
        }

        private static string[] GetAgeRanges(HashSet<string> nodeNames) =>
            nodeNames
                .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{2}[-+]"))
                .ToArray();

        private static string[] GetLocations(
            HashSet<string> nodeNames,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            var locationCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "city", "location", "region", "cityregion" };

            return catalog.Values
                .Where(n =>
                    nodeNames.Contains(n.NodeName) &&
                    locationCategories.Any(cat =>
                        n.Category.Contains(cat, StringComparison.OrdinalIgnoreCase)))
                .Select(n => n.NodeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
        }

        // ════════════════════════════════════════════════════════════════
        // DB QUERIES — using your real Visit model fields
        // ════════════════════════════════════════════════════════════════

        private async Task<List<VisitPatternDto>> GetVisitPatternsAsync()
        {
            try
            {
                // Pull raw data first — EF Core cannot translate DayOfWeek.ToString()
                // or Hour.ToString("00") to SQL. AsEnumerable() switches to client evaluation.
                var raw = await _context.Visits
                    .Select(v => new { v.VisitDateTime })
                    .ToListAsync();

                if (raw.Count == 0) return [];

                var grouped = raw
                    .GroupBy(v => new
                    {
                        Day = v.VisitDateTime.DayOfWeek.ToString(),
                        Hour = v.VisitDateTime.Hour.ToString("00") + ":00"
                    })
                    .Select(g => new { g.Key.Day, g.Key.Hour, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(5)
                    .ToList();

                int maxCount = grouped.Max(p => p.Count);

                return grouped.Select(p => new VisitPatternDto
                {
                    Day = p.Day,
                    Hour = p.Hour,
                    VisitCount = p.Count,
                    RelativeStrength = (float)p.Count / maxCount
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not compute visit patterns");
                return [];
            }
        }

        // ════════════════════════════════════════════════════════════════
        // BEHAVIOUR SUMMARY — one sentence the AI reads first
        // Built from structured data, not generated by AI
        // ════════════════════════════════════════════════════════════════

        private static string BuildSummary(
            int audienceSize,
            string engagement,
            string value,
            string recency,
            string loyalty,
            string spend,
            string[] locations,
            string[] ages,
            string? gender,
            float emailCoverage,
            float smsCoverage)
        {
            var parts = new List<string>();

            if (audienceSize > 0)
                parts.Add($"{audienceSize:N0} customers");

            if (gender != null)
                parts.Add(gender.ToLower());

            if (ages.Length > 0)
                parts.Add($"aged {string.Join(" or ", ages)}");

            if (locations.Length > 0)
                parts.Add($"in {string.Join(", ", locations)}");

            if (!string.IsNullOrEmpty(loyalty))
                parts.Add($"classified as {loyalty.ToLower()}");
            else if (!string.IsNullOrEmpty(recency))
                parts.Add($"last visited {recency.ToLower()}");

            if (!string.IsNullOrEmpty(spend))
                parts.Add($"previously spending {spend}");

            var summary = parts.Count > 0
                ? char.ToUpper(parts[0][0]) + string.Join(", ", parts)[1..] + "."
                : "Audience profile not available.";

            if (emailCoverage > 0)
                summary += $" {emailCoverage * 100:F0}% have a valid email address.";

            return summary;
        }

        // ════════════════════════════════════════════════════════════════
        // UTILITY
        // ════════════════════════════════════════════════════════════════

        private static HashSet<string> CollectNodeNames(
            SelectionGroupDto group,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (group.Rules != null)
                foreach (var rule in group.Rules)
                    if (catalog.TryGetValue(rule.TreeNodeId, out var node))
                        names.Add(node.NodeName);

            if (group.Groups != null)
                foreach (var child in group.Groups)
                    names.UnionWith(CollectNodeNames(child, catalog));

            return names;
        }
    }
}