using crm_ai.DTOs;
using crm_ai.Services;

namespace crm_ai.Helpers
{
    /// <summary>
    /// Fixes logically impossible AND combinations in AI-built rule trees.
    /// A customer can only have ONE value for single-value fields like age,
    /// gender, loyalty tier, spend range etc. If the AI puts multiple values
    /// from the same single-value field in an AND group, this converts them
    /// to an OR sub-group automatically.
    /// </summary>
    public static class SelectionSanitizer
    {
        private static readonly HashSet<string> SingleValueDataTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "agerange", "loyaltysegment", "visitrecency",
                "visitcount", "spendrange", "durationminutes", "daysago"
            };

        private static readonly HashSet<string> SingleValueParents =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Gender", "Segment",
                "Average transaction value",
                "Total transaction value",
                "Recency",
                "Total visits in last 12 months"
            };

        public static SelectionGroupDto Sanitize(
            SelectionGroupDto group,
            Dictionary<int, NodeCatalogItem> catalog,
            ILogger logger)
        {
            // Recurse into children first
            if (group.Groups != null)
                for (int i = 0; i < group.Groups.Count; i++)
                    group.Groups[i] = Sanitize(group.Groups[i], catalog, logger);

            // Only AND groups can have impossible combinations
            if (!string.Equals(group.LogicalOperator, "AND",
                    StringComparison.OrdinalIgnoreCase))
                return group;

            if (group.Rules == null || group.Rules.Count < 2)
                return group;

            // Find rules that share a single-value category
            var rulesByCategory = group.Rules
                .Where(r => catalog.ContainsKey(r.TreeNodeId))
                .GroupBy(r =>
                {
                    var node = catalog[r.TreeNodeId];
                    if (SingleValueDataTypes.Contains(node.DataType))
                        return $"type:{node.DataType}";
                    if (SingleValueParents.Contains(node.ParentName))
                        return $"parent:{node.ParentName}";
                    return null;
                })
                .Where(g => g.Key != null && g.Count() > 1)
                .ToList();

            if (rulesByCategory.Count == 0)
                return group;

            var remainingRules = group.Rules.ToList();
            var extraGroups = new List<SelectionGroupDto>();

            foreach (var categoryGroup in rulesByCategory)
            {
                var rules = categoryGroup.ToList();
                foreach (var rule in rules)
                    remainingRules.Remove(rule);

                extraGroups.Add(new SelectionGroupDto
                {
                    LogicalOperator = "OR",
                    Rules = rules,
                    Groups = new()
                });

                logger.LogInformation(
                    "Sanitizer: fixed impossible AND — moved {Count} " +
                    "'{Cat}' rules into OR sub-group",
                    rules.Count, categoryGroup.Key);
            }

            group.Rules = remainingRules;
            group.Groups = (group.Groups ?? new()).Concat(extraGroups).ToList();
            return group;
        }
    }
}