using crm_ai.DTOs;
using crm_ai.Services;

namespace crm_ai.Helpers
{
    // ════════════════════════════════════════════════════════════════════════
    // SELECTION MATCHER — pure static, zero tokens, fully unit testable
    //
    // WHAT WE SCORE AGAINST:
    //   Primary:  selection.Description  — rich AI-generated semantic text
    //   Fallback: selection.Name         — only when description is null/empty
    //   We never score against name when a description exists. Names are often
    //   cryptic ("Audience Set 3", "Q3 London F25-44"). Descriptions are truth.
    //
    // TWO TIERS:
    //   Tier 1 — Keyword overlap (0–100)
    //     Jaccard similarity between objective words and description words.
    //     Stop-words stripped. Word-boundary matched.
    //
    //   Tier 2 — Rule structure signals (0–60 bonus)
    //     Maps intent phrases → expected node categories in the actual rule tree.
    //     Works even when name/description are cryptic.
    //     e.g. "lapsed" in objective + Loyalty+Recency nodes in rules = +20
    //
    // THRESHOLD:
    //   CompositeScore below GoodMatchThreshold = no confident match.
    //   Caller should generate a prompt and offer building fresh.
    // ════════════════════════════════════════════════════════════════════════

    public static class SelectionMatcher
    {
        public const int GoodMatchThreshold = 25;

        public static int CompositeScore(
            string objective,
            string? description,
            string name,
            SelectionGroupDto rootGroup,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            var primaryText = string.IsNullOrWhiteSpace(description) ? name : description;
            return ScoreKeywordOverlap(objective, primaryText)
                 + ScoreRuleStructure(objective, rootGroup, catalog);
        }

        // ── Tier 1 ───────────────────────────────────────────────────────────

        private static int ScoreKeywordOverlap(string objective, string candidateText)
        {
            if (string.IsNullOrWhiteSpace(objective) ||
                string.IsNullOrWhiteSpace(candidateText)) return 0;

            var objWords = Tokenise(objective);
            var canWords = Tokenise(candidateText);
            if (objWords.Count == 0 || canWords.Count == 0) return 0;

            var intersection = objWords.Intersect(canWords).Count();
            var union = objWords.Union(canWords).Count();
            return (int)Math.Round((double)intersection / union * 100);
        }

        // ── Tier 2 ───────────────────────────────────────────────────────────

        private static int ScoreRuleStructure(
            string objective,
            SelectionGroupDto rootGroup,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            if (string.IsNullOrWhiteSpace(objective)) return 0;

            var usedCategories = CollectCategories(rootGroup, catalog);
            if (usedCategories.Count == 0) return 0;

            var objLower = objective.ToLower();
            int bonus = 0;

            foreach (var signal in Signals)
            {
                bool triggered = signal.Triggers.Any(t =>
                    objLower.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (!triggered) continue;

                bool structureMatches = signal.Categories.Any(expected =>
                    usedCategories.Any(cat =>
                        cat.Contains(expected, StringComparison.OrdinalIgnoreCase)));

                if (structureMatches) bonus += signal.Points;
            }

            return Math.Min(bonus, 60);
        }

        private static readonly (string[] Triggers, string[] Categories, int Points)[] Signals =
        [
            (
                Triggers: ["lapsed", "lapse", "win back", "winback", "re-engage",
                           "reengage", "inactive", "not visited", "haven't visited",
                           "havent visited", "bring back", "lost"],
                Categories: ["recency", "loyalty", "visit", "segment"],
                Points: 20
            ),
            (
                Triggers: ["loyal", "loyalty", "frequent", "vip",
                           "best customers", "repeat", "regular"],
                Categories: ["loyalty", "segment"],
                Points: 15
            ),
            (
                Triggers: ["high spend", "high value", "big spender",
                           "spend", "revenue", "£", "upsell", "premium", "transaction"],
                Categories: ["spend", "transaction", "purchase", "value"],
                Points: 15
            ),
            (
                Triggers: ["retention", "at risk", "at-risk", "churn", "retain"],
                Categories: ["recency", "loyalty", "visit", "segment"],
                Points: 15
            ),
            (
                Triggers: ["new customer", "first time", "never visited", "acquisition"],
                Categories: ["loyalty", "visit", "segment"],
                Points: 15
            ),
            (
                Triggers: ["london", "manchester", "birmingham", "leeds", "edinburgh",
                           "region", "location", "city", "area", "north", "south",
                           "scotland", "wales", "midlands"],
                Categories: ["location", "city", "region"],
                Points: 10
            ),
            (
                Triggers: ["female", "male", "women", "men", "ladies",
                           "aged", "age", "young", "older", "gender"],
                Categories: ["gender", "age", "demographic"],
                Points: 10
            ),
            (
                Triggers: ["email", "emailable", "newsletter"],
                Categories: ["contact", "email", "profile"],
                Points: 10
            ),
            (
                Triggers: ["sms", "smsable", "text message", "mobile"],
                Categories: ["sms", "contact", "profile"],
                Points: 10
            ),
        ];

        // ── Reason builder ────────────────────────────────────────────────────
        // Human-readable explanation of why this selection scored.
        // Pure C# — deterministic, never calls AI.

        public static string BuildReason(
            string objective,
            string? description,
            SelectionGroupDto rootGroup,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            var reasons = new List<string>();
            var objLower = objective.ToLower();
            var usedCategories = CollectCategories(rootGroup, catalog);

            var structureChecks = new[]
            {
                (Triggers: new[] { "lapsed", "lapse", "win back", "inactive" },
                 Categories: new[] { "loyalty", "recency", "segment" },
                 Label: "targets lapsed/inactive customers"),

                (Triggers: new[] { "loyal", "frequent", "vip" },
                 Categories: new[] { "loyalty", "segment" },
                 Label: "includes loyalty segment filter"),

                (Triggers: new[] { "spend", "high value", "£", "upsell" },
                 Categories: new[] { "spend", "transaction" },
                 Label: "has spend-based targeting"),

                (Triggers: new[] { "email", "emailable" },
                 Categories: new[] { "contact", "email" },
                 Label: "audience is emailable"),

                (Triggers: new[] { "sms", "smsable" },
                 Categories: new[] { "sms", "contact" },
                 Label: "audience has SMS opt-in"),

                (Triggers: new[] { "retention", "at risk", "churn" },
                 Categories: new[] { "recency", "loyalty" },
                 Label: "targets at-risk customers"),
            };

            foreach (var check in structureChecks)
            {
                bool triggered = check.Triggers.Any(t =>
                    objLower.Contains(t, StringComparison.OrdinalIgnoreCase));
                bool matches = check.Categories.Any(expected =>
                    usedCategories.Any(cat =>
                        cat.Contains(expected, StringComparison.OrdinalIgnoreCase)));
                if (triggered && matches)
                    reasons.Add(check.Label);
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                var descMatches = Tokenise(objective).Intersect(Tokenise(description))
                    .Take(3).ToList();
                if (descMatches.Count > 0)
                    reasons.Add($"description matches: {string.Join(", ", descMatches)}");
            }

            if (reasons.Count == 0) return "Closest available match";
            var result = string.Join("; ", reasons);
            return char.ToUpper(result[0]) + result[1..];
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        public static HashSet<string> CollectCategories(
            SelectionGroupDto group,
            Dictionary<int, NodeCatalogItem> catalog)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (group.Rules != null)
                foreach (var rule in group.Rules)
                    if (catalog.TryGetValue(rule.TreeNodeId, out var node))
                        cats.Add(node.Category);
            if (group.Groups != null)
                foreach (var child in group.Groups)
                    cats.UnionWith(CollectCategories(child, catalog));
            return cats;
        }

        internal static HashSet<string> Tokenise(string text) =>
            text.ToLower()
                .Split([' ', ',', '.', '-', '_', '/', '(', ')', '!', '?', ':'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !StopWords.Contains(w))
                .ToHashSet();

        private static readonly HashSet<string> StopWords = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "customers", "customer", "people", "person", "audience",
            "members", "member", "visitors", "visitor", "users", "user",
            "who", "that", "which", "the", "and", "but", "for", "with",
            "has", "have", "are", "were", "they", "their", "them",
            "all", "any", "some", "can", "not", "get", "want", "need",
            "find", "show", "our", "been", "this", "from", "will",
            "campaign", "selection", "target", "targeting", "send",
            "should", "would", "could", "using", "based", "only", "also",
            "special", "offer", "promote", "run", "create", "new","visited", "visit", "visiting", "last", "within", "days", "weeks"
        };
    }
}