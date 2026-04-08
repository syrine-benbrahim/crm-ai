using crm_ai.DTOs;
using System.Text;

namespace crm_ai.Helpers
{
    /// <summary>
    /// Converts an enriched SelectionGroupDto tree into a structured, human-readable
    /// prompt that Llama 4 Maverick can reason about to produce perfect descriptions.
    ///
    /// Enrichment contract (from AiService.EnrichWithNodeNamesAsync):
    ///   rule.Operator  = human-readable category label  (e.g. "Age range", "Gender")
    ///   rule.Value     = the actual filter value         (e.g. "25-34",   "Female")
    ///                    OR empty string if the node name IS the value (leaf nodes)
    ///   rule.TreeNodeId kept for reference only
    /// </summary>
    public static class SelectionPromptBuilder
    {
        // ─── Category friendly names (DataType → plain English category) ────
        private static readonly Dictionary<string, string> DataTypeLabels =
            new(StringComparer.OrdinalIgnoreCase)
        {
            { "agerange",        "Age range"              },
            { "string",          "Profile attribute"      },
            { "visitcount",      "Visit count"            },
            { "visitrecency",    "Visit recency"          },
            { "spendrange",      "Spend range"            },
            { "moneyrange",      "Spend range"            },
            { "cityregion",      "Location (region)"      },
            { "notnull",         "Contact availability"   },
            { "count",           "Engagement frequency"   },
            { "countdistinct",   "Locations visited"      },
            { "durationminutes", "Dwell time"             },
            { "daysago",         "Campaign recency"       },
            { "date",            "Campaign recency"       },
            { "dayofweek",       "Visit day"              },
            { "hourofday",       "Visit time"             },
            { "loyaltysegment",  "Loyalty segment"        },
            { "siteid",          "Site preference"        },
            { "boolean",         "Flag / consent"         },
            { "bool",            "Flag / consent"         },
            { "number",          "Numeric attribute"      },
        };

        // ─── Logical operator plain English ─────────────────────────────────
        private static string OpLabel(string? op) => (op ?? "AND").ToUpper() switch
        {
            "AND" => "ALL of the following must be true",
            "OR" => "ANY of the following can be true",
            "EXCLUDE" => "NONE of the following must be true (exclusion)",
            _ => op ?? "AND"
        };

        // ────────────────────────────────────────────────────────────────────
        // Public entry point
        // ────────────────────────────────────────────────────────────────────
        public static string BuildUserPrompt(EnrichedGroupDto rootGroup)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are given a CRM audience selection rule set.");
            sb.AppendLine("Each GROUP specifies whether ALL / ANY / NONE of its conditions must apply.");
            sb.AppendLine("Each RULE describes one filter criterion about a customer.");
            sb.AppendLine();
            sb.AppendLine("=== AUDIENCE RULES ===");
            sb.AppendLine();
            AppendGroup(sb, rootGroup, depth: 0);
            sb.AppendLine();
            sb.AppendLine("=== YOUR TASK ===");
            sb.AppendLine("Write a single, professional 1–3 sentence audience description.");
            sb.AppendLine("Rules:");
            sb.AppendLine("- Write for a marketing manager — plain English, no technical jargon.");
            sb.AppendLine("- Accurately reflect ALL rules including nested groups and exclusions.");
            sb.AppendLine("- For AND groups: use 'who also', 'and', 'with'.");
            sb.AppendLine("- For OR groups: use 'or', 'either'.");
            sb.AppendLine("- For EXCLUDE groups: use 'excluding', 'but not', 'except'.");
            sb.AppendLine("- Start with: 'Customers who...' or 'Audience targeting...'");
            sb.AppendLine("- Do NOT mention IDs, SQL, JSON, or technical terms.");
            sb.AppendLine("- Return ONLY the description. No preamble, no quotes, no markdown.");

            return sb.ToString();
        }

        // ────────────────────────────────────────────────────────────────────
        // Recursive group renderer
        // ────────────────────────────────────────────────────────────────────
        private static void AppendGroup(StringBuilder sb, EnrichedGroupDto group, int depth)
        {
            string pad = new string(' ', depth * 3);
            string opText = OpLabel(group.LogicalOperator);

            if (depth == 0)
                sb.AppendLine($"ROOT GROUP — {opText}:");
            else
                sb.AppendLine($"{pad}SUB-GROUP ({opText}):");

            // Rules
            if (group.Rules != null)
            {
                foreach (var rule in group.Rules)
                {
                    string category = DataTypeLabels.TryGetValue(rule.DataType ?? "", out var lbl)
                        ? lbl : rule.Category;

                    string value = string.IsNullOrWhiteSpace(rule.Value)
                        ? rule.NodeName   // leaf node: name IS the value
                        : rule.Value;     // runtime value supplied by user (e.g. SiteId)

                    sb.AppendLine($"{pad}  • [{category}] {rule.NodeName}" +
                        (string.IsNullOrWhiteSpace(rule.Value) ? "" : $" = {value}"));
                }
            }

            // Nested sub-groups
            if (group.Groups != null)
            {
                foreach (var child in group.Groups)
                    AppendGroup(sb, child, depth + 1);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Enriched DTOs used only inside the AI pipeline (never exposed via API)
    // ────────────────────────────────────────────────────────────────────────

    public class EnrichedGroupDto
    {
        public string? LogicalOperator { get; set; }
        public List<EnrichedRuleDto>? Rules { get; set; }
        public List<EnrichedGroupDto>? Groups { get; set; }
    }

    public class EnrichedRuleDto
    {
        public int TreeNodeId { get; set; }
        public string NodeName { get; set; } = "";   // e.g. "Female", "25-34", "London"
        public string Category { get; set; } = "";   // e.g. "Gender", "Age", "Region"
        public string? DataType { get; set; }        // e.g. "agerange", "string"
        public string? Value { get; set; }           // runtime value if applicable
    }
}