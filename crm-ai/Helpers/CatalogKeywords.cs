using crm_ai.Services;

namespace crm_ai.Helpers
{
    public static class CatalogKeywords
    {
        public static Dictionary<string, HashSet<string>> Build(
            Dictionary<int, NodeCatalogItem> catalog)
        {
            var result = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var node in catalog.Values)
            {
                if (string.IsNullOrWhiteSpace(node.Category)) continue;

                if (!result.TryGetValue(node.Category, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[node.Category] = set;
                }

                var words = node.SearchText
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2);

                foreach (var w in words)
                    set.Add(w);
            }

            var augments = new Dictionary<string, string[]>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Recency"] = new[] { "came", "come", "back", "returned",
                    "haven", "since", "often", "active", "inactive" },
                ["Visits"] = new[] { "come", "came", "often", "times",
                    "many", "repeat", "multiple" },
                ["Loyalty"] = new[] { "often", "come", "win", "lost",
                    "churned", "dormant", "retention", "tier" },
                ["Spend"] = new[] { "bought", "buy", "money", "over",
                    "under", "budget", "£", "pound" },
                ["Age"] = new[] { "young", "old", "born", "generation",
                    "teen", "elderly", "youth", "older", "younger" },
                ["Gender"] = new[] { "ladies", "gents", "boys", "girls", "sex" },
                ["Location"] = new[] { "live", "living", "from",
                    "based", "nearby", "local", "where" },
                ["Dwell Time"] = new[] { "stay", "long", "quick",
                    "fast", "slow", "browsing", "inside" },
                ["Visit Pattern"] = new[] { "when", "typical", "usual",
                    "peak", "off", "busy", "quiet", "lunch", "late", "early" },
                ["Email Engagement"] = new[] { "read", "inbox", "mailing",
                    "unsubscribed", "responded", "newsletter" },
                ["SMS Engagement"] = new[] { "texted", "received",
                    "delivered", "undelivered" },
                ["Contact"] = new[] { "reach", "reachable", "opted",
                    "communicate", "channel" },
                ["Site"] = new[] { "shop", "usual", "interacted" },
                ["Profile"] = new[] { "holder", "participant",
                    "valid", "invalid", "consent" },
                ["Transaction values"] = new[] { "spend", "spent", "spending",
                    "lot", "much", "over", "under", "money", "£", "pound",
                    "high", "low" },
            };

            foreach (var (cat, synonyms) in augments)
            {
                if (!result.TryGetValue(cat, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[cat] = set;
                }
                foreach (var s in synonyms)
                    set.Add(s);
            }

            return result;
        }
    }
}