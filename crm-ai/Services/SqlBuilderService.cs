using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Helpers;
using crm_ai.Models;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace crm_ai.Services
{
    public class SqlBuilderService : ISqlBuilderService
    {
        private readonly AppDbContext _context;

        private static readonly HashSet<string> AllowedLogicalOperators = new(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "OR", "EXCLUDE"
        };

        private static readonly Dictionary<string, string> EntityAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Customer", "c" },
            { "CustomerAddress", "a" },
            { "Transaction", "t" }
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

            if (dataType == "visitcount")
            {
                if (value.EndsWith("+"))
                {
                    var num = SafeNumber(value.Replace("+", ""));
                    return $"(SELECT COUNT(*) FROM Visits v WHERE v.CustomerId = c.Id) >= {num}";
                }
                return $"(SELECT COUNT(*) FROM Visits v WHERE v.CustomerId = c.Id) = {SafeNumber(value)}";
            }

            if (dataType == "visitrecency")
            {
                return value switch
                {
                    "Yesterday" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) >= DATEADD(DAY,-1,GETDATE())",
                    "<= 7 days" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) >= DATEADD(DAY,-7,GETDATE())",
                    "8-14 days" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) BETWEEN DATEADD(DAY,-14,GETDATE()) AND DATEADD(DAY,-8,GETDATE())",
                    "15-31 days" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) BETWEEN DATEADD(DAY,-31,GETDATE()) AND DATEADD(DAY,-15,GETDATE())",
                    "1-2 months" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) BETWEEN DATEADD(MONTH,-2,GETDATE()) AND DATEADD(MONTH,-1,GETDATE())",
                    "2-3 months" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) BETWEEN DATEADD(MONTH,-3,GETDATE()) AND DATEADD(MONTH,-2,GETDATE())",
                    "3-4 Months" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) BETWEEN DATEADD(MONTH,-4,GETDATE()) AND DATEADD(MONTH,-3,GETDATE())",
                    "4 months +" => "(SELECT MAX(v.VisitDateTime) FROM Visits v WHERE v.CustomerId = c.Id) <= DATEADD(MONTH,-4,GETDATE())",
                    _ => string.Empty
                };
            }

            if (dataType == "cityregion")
            {
                var regionCities = GetRegionCities(value);
                if (!regionCities.Any())
                    return string.Empty;

                var citiesSql = string.Join(", ", regionCities.Select(c => $"'{c}'"));
                return $"a.City IN ({citiesSql})";
            }

            if (dataType == "number")
            {
                if (value.Contains("-"))
                {
                    var parts = value.Split("-");
                    if (parts.Length != 2)
                        throw new ArgumentException($"Invalid range value: {value}");
                    return $"{field} BETWEEN {SafeNumber(parts[0])} AND {SafeNumber(parts[1])}";
                }

                if (value.EndsWith("+"))
                    return $"{field} >= {SafeNumber(value.Replace("+", ""))}";

                return SqlOperatorMapper.MapNumber(op, field, SafeNumber(value));
            }

            if (dataType == "spendrange")
            {
                var cleanValue = value.Replace("£", "").Replace("ú", "").Trim();

                if (cleanValue.StartsWith("<"))
                    return $"{field} < {SafeNumber(cleanValue.Replace("<", "").Trim())}";

                if (cleanValue.EndsWith("+"))
                    return $"{field} >= {SafeNumber(cleanValue.Replace("+", "").Trim())}";

                if (cleanValue.Contains("-"))
                {
                    var parts = cleanValue.Split("-");
                    if (parts.Length == 2)
                        return $"{field} BETWEEN {SafeNumber(parts[0].Trim())} AND {SafeNumber(parts[1].Trim())}";
                }

                return string.Empty;
            }

            if (dataType == "date")
                return $"{field} {op} '{Escape(value)}'";

            if (dataType == "bool")
                return $"{field} = {(value.ToLower() == "true" ? 1 : 0)}";

            return SqlOperatorMapper.MapString(op, field, Escape(value));
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
                joins.Add("LEFT JOIN CustomerAddresses a ON a.CustomerId = c.Id");

            if (usedEntities.Contains("Transaction"))
                joins.Add("LEFT JOIN Transactions t ON t.CustomerId = c.Id");

            return string.Join(" ", joins);
        }

        private static string Escape(string value)
        {
            return value.Replace("'", "''");
        }

        private static bool IsValidFieldName(string? field)
        {
            return !string.IsNullOrWhiteSpace(field) &&
                   field.All(c => char.IsLetterOrDigit(c) || c == '_');
        }

        private static List<string> GetRegionCities(string regionName)
        {
            return regionName switch
            {
                "East" => new List<string> { "Cambridge", "Ipswich", "Norwich", "Peterborough" },
                "London" => new List<string> { "London E", "London EC", "London N", "London NW", "London SE", "London SW", "London W", "London WC" },
                "Midlands" => new List<string> { "Birmingham", "Coventry", "Derby", "Dudley", "Hereford", "Leicester", "Lincoln", "Nottingham", "Northampton", "Stoke-On-Trent", "Shrewsbury", "Telford", "Worcester", "Walsall", "Wolverhampton" },
                "N.Ireland" => new List<string> { "Belfast" },
                "North East" => new List<string> { "Bradford", "Durham", "Darlington", "Doncaster", "Huddersfield", "Harrogate", "Hull", "Halifax", "Leeds", "Newcastle Upon Tyne", "Sheffield", "Sunderland", "Cleveland", "Wakefield", "York" },
                "North West" => new List<string> { "Blackburn", "Bolton", "Carlisle", "Chester", "Crewe", "Blackpool", "Liverpool", "Lancaster", "Manchester", "Oldham", "Preston", "Stockport", "Warrington", "Wigan" },
                "Scotland" => new List<string> { "Aberdeen", "Dundee", "Dumfries and Galloway", "Edinburgh", "Falkirk and Stirling", "Glasgow", "Outer Hebrides", "Inverness", "Kilmarnock", "Kirkwall", "Kirkcaldy", "Motherwell", "Paisley", "Perth", "Galashiels", "Shetland" },
                "South East" => new List<string> { "St. Albans", "Brighton", "Bromley", "Chelmsford", "Colchester", "Croydon", "Canterbury", "Dartford", "Enfield", "Harrow", "Hemel Hempstead", "Ilford", "Kingston upon Thames", "Luton", "Rochester", "Milton Keynes", "Oxford", "Portsmouth", "Reading", "Redhill", "Romford", "Stevenage", "Slough", "Sutton", "Southampton", "Southend-on-Sea", "Tonbridge", "Twickenham", "Southall", "Watford" },
                "South West" => new List<string> { "Bath", "Bournemouth", "Bristol", "Dorchester", "Exeter", "Gloucester", "Guildford", "Plymouth", "Swindon", "Salisbury", "Taunton", "Newton Abbot", "Truro", "Torquay" },
                "Wales" => new List<string> { "Cardiff", "Brecon", "Llandudno", "Newport", "Swansea" },
                _ => new List<string>()
            };
        }

        private static string SafeNumber(string value)
        {
            value = value.Trim();
            if (decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);

            throw new ArgumentException($"Invalid numeric value: {value}");
        }
    }
}