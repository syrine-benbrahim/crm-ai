using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Models;
using Microsoft.EntityFrameworkCore;

namespace crm_ai.Services
{
    public class SqlBuilderService
    {
        private readonly AppDbContext _context;

        private static readonly HashSet<string> AllowedLogicalOperators = new(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "OR", "EXCLUDE"
        };

        // 🔥 Entity → Alias mapping
        private static readonly Dictionary<string, string> EntityAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Customer", "c" },
            { "CustomerAddress", "a" }
        };

        public SqlBuilderService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<(string WhereClause, string JoinClause)> BuildQueryPartsAsync(SelectionGroupDto group)
        {
            var usedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var where = await BuildGroupAsync(group, usedEntities);

            var joins = BuildJoinClause(usedEntities);

            return (where, joins);
        }

        private async Task<string> BuildGroupAsync(SelectionGroupDto group, HashSet<string> usedEntities)
        {
            if (group == null)
                throw new ArgumentNullException(nameof(group));

            var conditions = new List<string>();

            string logicalOperator = (group.LogicalOperator ?? "AND").Trim().ToUpper();

            if (!AllowedLogicalOperators.Contains(logicalOperator))
                throw new ArgumentException($"Invalid logical operator: {group.LogicalOperator}");

            Dictionary<int, TreeNode> nodeMap = new();

            if (group.Rules != null && group.Rules.Any())
            {
                var ids = group.Rules.Select(r => r.TreeNodeId).Distinct().ToList();

                nodeMap = await _context.TreeNodes
                    .Where(x => ids.Contains(x.Id))
                    .ToDictionaryAsync(x => x.Id);
            }

            if (group.Rules != null)
            {
                foreach (var rule in group.Rules)
                {
                    if (!nodeMap.TryGetValue(rule.TreeNodeId, out var node))
                        continue;

                    if (!IsValidFieldName(node.FieldName))
                        throw new ArgumentException($"Invalid field name: {node.FieldName}");

                    usedEntities.Add(node.EntityName ?? "Customer");

                    var condition = BuildCondition(node, rule);
                    if (!string.IsNullOrWhiteSpace(condition))
                        conditions.Add(condition);
                }
            }

            if (group.Groups != null)
            {
                foreach (var sub in group.Groups)
                {
                    var subQuery = await BuildGroupAsync(sub, usedEntities);
                    if (!string.IsNullOrWhiteSpace(subQuery))
                        conditions.Add($"({subQuery})");
                }
            }

            if (!conditions.Any())
                return "1=1";

            if (logicalOperator == "EXCLUDE")
                return string.Join(" AND ", conditions.Select(c => $"NOT ({c})"));

            return string.Join($" {logicalOperator} ", conditions);
        }

        private string BuildCondition(TreeNode node, SelectionRuleDto rule)
        {
            string field = GetQualifiedField(node);
            string dataType = node.DataType?.Trim().ToLower() ?? "string";
            string value = rule.Value?.Trim() ?? "";
            string op = rule.Operator?.Trim().ToUpper() ?? "=";

            // 🔥 Visit Count
            if (dataType == "visitcount")
            {
                if (value.EndsWith("+"))
                {
                    var num = int.Parse(value.Replace("+", ""));
                    return $"(SELECT COUNT(*) FROM Visits v WHERE v.CustomerId = c.Id) >= {num}";
                }

                return $"(SELECT COUNT(*) FROM Visits v WHERE v.CustomerId = c.Id) = {value}";
            }

            // 🔥 Visit Recency
            if (dataType == "visitrecency")
            {
                return value switch
                {
                    "Yesterday" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) >= DATEADD(DAY,-1,GETDATE())",
                    "<= 7 days" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) >= DATEADD(DAY,-7,GETDATE())",
                    _ => "1=1"
                };
            }

            if (dataType == "number")
            {
                if (value.Contains("-"))
                {
                    var parts = value.Split("-");
                    return $"{field} BETWEEN {parts[0]} AND {parts[1]}";
                }

                if (value.EndsWith("+"))
                {
                    var num = value.Replace("+", "");
                    return $"{field} >= {num}";
                }

                return $"{field} {op} {value}";
            }

            if (dataType == "date")
                return $"{field} {op} '{Escape(value)}'";

            if (dataType == "bool")
                return $"{field} = {(value.ToLower() == "true" ? 1 : 0)}";

            return op switch
            {
                "CONTAINS" => $"{field} LIKE '%{Escape(value)}%'",
                "STARTS WITH" => $"{field} LIKE '{Escape(value)}%'",
                "ENDS WITH" => $"{field} LIKE '%{Escape(value)}'",
                _ => $"{field} {op} '{Escape(value)}'"
            };
        }

        private string GetQualifiedField(TreeNode node)
        {
            var entity = node.EntityName ?? "Customer";

            if (!EntityAliases.TryGetValue(entity, out var alias))
                alias = "c";

            return $"{alias}.{node.FieldName}";
        }

        private string BuildJoinClause(HashSet<string> usedEntities)
        {
            var joins = new List<string>();

            if (usedEntities.Contains("CustomerAddress"))
            {
                joins.Add("LEFT JOIN CustomerAddresses a ON a.CustomerId = c.Id");
            }

            return string.Join(" ", joins);
        }

        private static string Escape(string value)
        {
            return value.Replace("'", "''");
        }

        private static bool IsValidFieldName(string field)
        {
            return !string.IsNullOrWhiteSpace(field) &&
                   field.All(c => char.IsLetterOrDigit(c) || c == '_');
        }
    }
}