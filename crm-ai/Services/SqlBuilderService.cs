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

        // Normalise all entity name variants (singular/plural both appear in the CSV)
        // to a canonical table name used in raw SQL.
        private static string NormaliseEntity(string? raw) => (raw ?? "Customers").Trim() switch
        {
            var s when s.Equals("Customer", StringComparison.OrdinalIgnoreCase) => "Customers",
            var s when s.Equals("CustomerAddress", StringComparison.OrdinalIgnoreCase) => "CustomerAddresses",
            var s when s.Equals("Transaction", StringComparison.OrdinalIgnoreCase) => "Transactions",
            var s when s.Equals("Visit", StringComparison.OrdinalIgnoreCase) => "Visits",
            var s when s.Equals("Booking", StringComparison.OrdinalIgnoreCase) => "Bookings",
            var s => s   // already correct plural form
        };

        // SQL alias for each canonical table name
        private static readonly Dictionary<string, string> TableAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Customers",         "c" },
            { "CustomerAddresses", "a" },
            { "Transactions",      "t" },
            { "Visits",            "v" },
            { "Sites",             "s" },
            { "Bookings",          "b" }
        };

        public SqlBuilderService(AppDbContext context)
        {
            _context = context;
        }

        // ─────────────────────────────────────────────────────────
        // Public entry point
        // ─────────────────────────────────────────────────────────
        public async Task<(string WhereClause, string JoinClause)> BuildQueryPartsAsync(SelectionGroupDto group)
        {
            var usedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var where = await BuildGroupAsync(group, usedTables);
            var joins = BuildJoinClause(usedTables);
            return (where, joins);
        }

        // ─────────────────────────────────────────────────────────
        // Recursive group builder
        // ─────────────────────────────────────────────────────────
        private async Task<string> BuildGroupAsync(SelectionGroupDto group, HashSet<string> usedTables)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));

            string logicalOperator = (group.LogicalOperator ?? "AND").Trim().ToUpper();
            if (!AllowedLogicalOperators.Contains(logicalOperator))
                throw new ArgumentException($"Invalid logical operator: {group.LogicalOperator}");

            // Batch-load all TreeNodes referenced in this group's rules
            Dictionary<int, TreeNode> nodeMap = new();
            if (group.Rules != null && group.Rules.Any())
            {
                var ids = group.Rules.Select(r => r.TreeNodeId).Distinct().ToList();
                nodeMap = await _context.TreeNodes
                    .Where(x => ids.Contains(x.Id))
                    .ToDictionaryAsync(x => x.Id);
            }

            var conditions = new List<string>();

            // Rules
            if (group.Rules != null)
            {
                foreach (var rule in group.Rules)
                {
                    if (!nodeMap.TryGetValue(rule.TreeNodeId, out var node)) continue;
                    if (string.IsNullOrWhiteSpace(node.EntityName)) continue;
                    if (!string.IsNullOrWhiteSpace(node.FieldName) && !IsValidFieldName(node.FieldName))
                        throw new ArgumentException($"Invalid field name: {node.FieldName}");

                    // Track the canonical table so we can build JOINs later
                    usedTables.Add(NormaliseEntity(node.EntityName));

                    var condition = BuildCondition(node, rule);
                    if (!string.IsNullOrWhiteSpace(condition))
                        conditions.Add(condition);
                }
            }

            // Sub-groups
            if (group.Groups != null)
            {
                foreach (var sub in group.Groups)
                {
                    var subSql = await BuildGroupAsync(sub, usedTables);
                    if (!string.IsNullOrWhiteSpace(subSql))
                        conditions.Add($"({subSql})");
                }
            }

            if (!conditions.Any()) return "1=1";

            if (logicalOperator == "EXCLUDE")
                return string.Join(" AND ", conditions.Select(c => $"NOT ({c})"));

            return string.Join($" {logicalOperator} ", conditions);
        }

        // ─────────────────────────────────────────────────────────
        // Route to the right condition builder by DataType
        // The value used in conditions is always the NodeName because
        // selectable leaf nodes encode their filter value in NodeName.
        // rule.Value is only used when the UI supplies a runtime value
        // (e.g. a specific SiteId chosen by the user).
        // ─────────────────────────────────────────────────────────
        private string BuildCondition(TreeNode node, SelectionRuleDto rule)
        {
            string nodeName = node.NodeName?.Trim() ?? "";
            string dataType = node.DataType?.Trim().ToLower() ?? "string";
            string op = rule.Operator?.Trim().ToUpper() ?? "=";

            // Runtime value from the UI (used for SiteId, Boolean etc.)
            string ruleValue = rule.Value?.Trim() ?? "";

            return dataType switch
            {
                // ── original types (unchanged from old working service) ─
                "visitcount" => BuildVisitCountCondition(nodeName),
                "visitrecency" => BuildVisitRecencyCondition(nodeName),
                "spendrange" => BuildSpendRangeCondition(node, nodeName),
                "cityregion" => BuildCityRegionCondition(nodeName),
                "number" => BuildNumberCondition(node, nodeName, op), // kept as fallback
                "date" => BuildDateAsRecencyCondition(nodeName),
                "bool" => BuildBooleanCondition(node, ruleValue),

                // ── new types from updated TreeNode data ──────────────
                "agerange" => BuildAgeRangeCondition(nodeName),
                "notnull" => BuildNotNullCondition(node),
                "count" => BuildCountCondition(nodeName),
                "countdistinct" => BuildCountDistinctCondition(node, nodeName),
                "durationminutes" => BuildDurationCondition(nodeName),
                "daysago" => BuildDateAsRecencyCondition(nodeName),   // same logic, reuse
                "moneyrange" => BuildSpendRangeCondition(node, nodeName),// alias
                "dayofweek" => BuildDayOfWeekCondition(nodeName),
                "hourofday" => BuildHourOfDayCondition(nodeName),
                "loyaltysegment" => BuildLoyaltySegmentCondition(nodeName),
                "siteid" => BuildSiteIdCondition(node, ruleValue),
                "boolean" => BuildBooleanCondition(node, ruleValue),

                // ── default: plain string / email-domain match ────────
                "string" => BuildStringCondition(node, nodeName, op),
                _ => BuildStringCondition(node, nodeName, op)
            };
        }

        // ─────────────────────────────────────────────────────────
        // Condition builders
        // ─────────────────────────────────────────────────────────

        /// Age as a numeric column called "Age" (nodes 4-10, EntityName=Customer, FieldName=Age)
        /// NodeName holds the range label e.g. "18-24", "65+", "Unknown"
        private string BuildNumberCondition(TreeNode node, string nodeName, string op)
        {
            string field = GetQualifiedField(node);

            if (nodeName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return $"{field} IS NULL";

            if (nodeName.EndsWith("+"))
                return $"{field} >= {SafeNumber(nodeName.Replace("+", "").Trim())}";

            if (nodeName.Contains("-"))
            {
                var parts = nodeName.Split("-");
                if (parts.Length == 2)
                    return $"{field} BETWEEN {SafeNumber(parts[0].Trim())} AND {SafeNumber(parts[1].Trim())}";
            }

            return SqlOperatorMapper.MapNumber(op, field, SafeNumber(nodeName));
        }

        /// Age range condition – used by nodes 4-10 AND node 5467.
        /// All use EntityName=Customers, FieldName=BirthDate, DataType=agerange.
        /// NodeName is the label e.g. "18-24", "65+", "Unknown", "Under 18".
        private static string BuildAgeRangeCondition(string nodeName) => nodeName switch
        {
            "Under 18" => "DATEDIFF(YEAR, c.BirthDate, GETDATE()) < 18",
            "18-24" => "DATEDIFF(YEAR, c.BirthDate, GETDATE()) BETWEEN 18 AND 24",
            "25-34" => "DATEDIFF(YEAR, c.BirthDate, GETDATE()) BETWEEN 25 AND 34",
            "35-44" => "DATEDIFF(YEAR, c.BirthDate, GETDATE()) BETWEEN 35 AND 44",
            "45-54" => "DATEDIFF(YEAR, c.BirthDate, GETDATE()) BETWEEN 45 AND 54",
            "55-64" => "DATEDIFF(YEAR, c.BirthDate, GETDATE()) BETWEEN 55 AND 64",
            "65+" => "DATEDIFF(YEAR, c.BirthDate, GETDATE()) >= 65",
            "Unknown" => "c.BirthDate IS NULL",
            _ => "1=1"
        };

        /// Gender / FirstName / Country / individual City / Email plain match
        private string BuildStringCondition(TreeNode node, string value, string op)
        {
            // Email domain nodes (EntityName=Customer, FieldName=Email) use pattern matching
            if (string.Equals(node.FieldName, "Email", StringComparison.OrdinalIgnoreCase)
                && IsEmailDomainValue(value))
                return BuildEmailDomainCondition(value);

            string field = GetQualifiedField(node);
            return SqlOperatorMapper.MapString(op, field, Escape(value));
        }

        /// Email domain pattern matching – NodeName is the provider label
        private static string BuildEmailDomainCondition(string domain) => domain switch
        {
            "Microsoft (HOTMAIL)" => "(c.Email LIKE '%@hotmail.%' OR c.Email LIKE '%@outlook.%' OR c.Email LIKE '%@live.%')",
            "Yahoo" => "(c.Email LIKE '%@yahoo.%' OR c.Email LIKE '%@ymail.%')",
            "AOL" => "c.Email LIKE '%@aol.%'",
            "Gmail" => "c.Email LIKE '%@gmail.%'",
            "Student" => "(c.Email LIKE '%@%.ac.uk' OR c.Email LIKE '%@%.edu')",
            "Non-Profits" => "c.Email LIKE '%@%.org'",
            "Other Personal" => "(c.Email NOT LIKE '%@%.com' AND c.Email NOT LIKE '%@%.co.uk')",
            "Business" => "(c.Email LIKE '%@%.com' OR c.Email LIKE '%@%.co.uk')",
            _ => "1=1"
        };

        private static bool IsEmailDomainValue(string v) =>
            v is "Microsoft (HOTMAIL)" or "Yahoo" or "AOL" or "Gmail"
               or "Student" or "Non-Profits" or "Other Personal" or "Business";

        /// NOT NULL check – communication availability nodes (5175-5181)
        private string BuildNotNullCondition(TreeNode node)
        {
            string field = GetQualifiedField(node);
            return $"({field} IS NOT NULL AND {field} != '')";
        }

        /// City region – NodeName is the region label e.g. "London", "Midlands"
        private static string BuildCityRegionCondition(string regionName)
        {
            var cities = GetRegionCities(regionName);
            if (!cities.Any()) return "1=1";
            var list = string.Join(", ", cities.Select(c => $"'{Escape(c)}'"));
            return $"a.City IN ({list})";
        }

        /// Visit count (nodes 5188-5197) – NodeName is the exact count e.g. "1", "10 +"
        private static string BuildVisitCountCondition(string nodeName)
        {
            if (nodeName.Contains("+"))
                return $"(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) >= {SafeNumber(nodeName.Replace("+", "").Trim())}";

            return $"(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) = {SafeNumber(nodeName.Trim())}";
        }

        /// Campaign engagement count (nodes 5407-5425) – NodeName is word-based
        private static string BuildCountCondition(string nodeName) => nodeName switch
        {
            "Once" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) = 1",
            "Twice" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) = 2",
            "3-5 Times" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) BETWEEN 3 AND 5",
            "6-10 Times" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) BETWEEN 6 AND 10",
            "11-15 Times" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) BETWEEN 11 AND 15",
            "16-25 Times" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) BETWEEN 16 AND 25",
            "26-50 Times" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) BETWEEN 26 AND 50",
            "51+" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id) >= 51",
            "Open Emails in the Last 6 Month" =>
                "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id AND VisitDateTime >= DATEADD(MONTH,-6,GETDATE())) >= 1",
            _ => "1=1"
        };

        /// Distinct site count (nodes 5790-5799) – NodeName is "1".."9", "10 or more"
        private string BuildCountDistinctCondition(TreeNode node, string nodeName)
        {
            string field = node.FieldName ?? "SiteId";
            string table = NormaliseEntity(node.EntityName);

            if (nodeName.Equals("10 or more", StringComparison.OrdinalIgnoreCase))
                return $"(SELECT COUNT(DISTINCT {field}) FROM {table} WHERE CustomerId = c.Id) >= 10";

            if (nodeName.Contains("+"))
                return $"(SELECT COUNT(DISTINCT {field}) FROM {table} WHERE CustomerId = c.Id) >= {SafeNumber(nodeName.Replace("+", "").Trim())}";

            return $"(SELECT COUNT(DISTINCT {field}) FROM {table} WHERE CustomerId = c.Id) = {SafeNumber(nodeName.Trim())}";
        }

        /// Visit recency (nodes 5349-5356) – NodeName is the label
        private static string BuildVisitRecencyCondition(string nodeName) => nodeName switch
        {
            "Yesterday" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) >= DATEADD(DAY,-1,GETDATE())",
            "<= 7 days" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) >= DATEADD(DAY,-7,GETDATE())",
            "8-14 days" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(DAY,-14,GETDATE()) AND DATEADD(DAY,-8,GETDATE())",
            "15-31 days" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(DAY,-31,GETDATE()) AND DATEADD(DAY,-15,GETDATE())",
            "1-2 months" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(MONTH,-2,GETDATE()) AND DATEADD(MONTH,-1,GETDATE())",
            "2-3 months" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(MONTH,-3,GETDATE()) AND DATEADD(MONTH,-2,GETDATE())",
            "3-4 Months" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(MONTH,-4,GETDATE()) AND DATEADD(MONTH,-3,GETDATE())",
            "4 months +" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) <= DATEADD(MONTH,-4,GETDATE())",
            _ => "1=1"
        };

        /// Campaign recency – DataType="date" (nodes 5396-5405) AND DataType="DaysAgo" (5385-5394, 5427-5458)
        /// Both share identical NodeName labels so one method handles both.
        private static string BuildDateAsRecencyCondition(string nodeName) => nodeName switch
        {
            "< 7 Days Ago" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) >= DATEADD(DAY,-7,GETDATE())",
            "7-14 Days Ago" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(DAY,-14,GETDATE()) AND DATEADD(DAY,-7,GETDATE())",
            "15-30 Days Ago" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(DAY,-30,GETDATE()) AND DATEADD(DAY,-15,GETDATE())",
            "31-60 Days Ago" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(DAY,-60,GETDATE()) AND DATEADD(DAY,-31,GETDATE())",
            "61 -90 Days Ago" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(DAY,-90,GETDATE()) AND DATEADD(DAY,-61,GETDATE())",
            "91-180 Days Ago" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(DAY,-180,GETDATE()) AND DATEADD(DAY,-91,GETDATE())",
            "181- 365 Days Ago" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(DAY,-365,GETDATE()) AND DATEADD(DAY,-181,GETDATE())",
            "1-2 Years" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(YEAR,-2,GETDATE()) AND DATEADD(YEAR,-1,GETDATE())",
            "2-3 Years" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(YEAR,-3,GETDATE()) AND DATEADD(YEAR,-2,GETDATE())",
            "3+ Years" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) <= DATEADD(YEAR,-3,GETDATE())",
            _ => "1=1"
        };

        /// Dwell time (nodes 5201-5211) – NodeName is the label.
        /// SQL Server does not allow GROUP BY inside a scalar subquery used in WHERE.
        /// Fix: wrap the grouped subquery in an outer SELECT AVG(...) FROM (...) AS daily.
        private static string BuildDurationCondition(string nodeName)
        {
            // Inner query: minutes spent per day for this customer
            const string inner =
                "SELECT DATEDIFF(MINUTE, MIN(VisitDateTime), MAX(VisitDateTime)) AS DayMinutes " +
                "FROM Visits WHERE CustomerId = c.Id " +
                "GROUP BY CAST(VisitDateTime AS DATE)";

            // Outer scalar subquery: average of those daily durations
            const string avgExpr = $"(SELECT AVG(DayMinutes) FROM ({inner}) AS _daily)";

            return nodeName switch
            {
                "Less than 10 mins" => $"{avgExpr} < 10",
                "11-30 mins" => $"{avgExpr} BETWEEN 11 AND 30",
                "31-60 mins" => $"{avgExpr} BETWEEN 31 AND 60",
                "61 -120 mins" => $"{avgExpr} BETWEEN 61 AND 120",
                "120+ mins" => $"{avgExpr} >= 120",
                _ => "1=1"
            };
        }

        /// Spend range (nodes 5360-5382) – NodeName e.g. "<£10", "£10-£20", "£600+"
        private string BuildSpendRangeCondition(TreeNode node, string nodeName)
        {
            string field = GetQualifiedField(node);
            var clean = nodeName.Replace("£", "").Replace("ú", "").Trim();

            if (clean.StartsWith("<"))
                return $"{field} < {SafeNumber(clean.Replace("<", "").Trim())}";

            if (clean.EndsWith("+"))
                return $"{field} >= {SafeNumber(clean.Replace("+", "").Trim())}";

            if (clean.Contains("-"))
            {
                var parts = clean.Split("-");
                if (parts.Length == 2)
                    return $"{field} BETWEEN {SafeNumber(parts[0].Trim())} AND {SafeNumber(parts[1].Trim())}";
            }

            return "1=1";
        }

        /// Day of week (nodes 5475-5481, 5489-5495) – NodeName is day name
        private static string BuildDayOfWeekCondition(string nodeName)
        {
            // DATEPART(WEEKDAY,...): 1=Sunday, 2=Monday … 7=Saturday
            int day = nodeName switch
            {
                "Sunday" => 1,
                "Monday" => 2,
                "Tuesday" => 3,
                "Wednesday" => 4,
                "Thursday" => 5,
                "Friday" => 6,
                "Saturday" => 7,
                _ => 0
            };
            if (day == 0) return "1=1";
            return $"EXISTS (SELECT 1 FROM Visits WHERE CustomerId = c.Id AND DATEPART(WEEKDAY, VisitDateTime) = {day})";
        }

        /// Hour of day (nodes 5483-5487, 5497-5501) – NodeName is the time range label
        private static string BuildHourOfDayCondition(string nodeName)
        {
            var (from, to) = nodeName switch
            {
                "10:00 - 12:00" => (10, 11),
                "12:00 - 15:00" => (12, 14),
                "15-00 - 18:00" or "15:00 - 18:00" => (15, 17),
                "18:00 - 21:00" or "18:00  - 21:00" => (18, 20),
                "21:00 - 24:00" or "21:00  - 24:00" => (21, 23),
                _ => (0, 0)
            };
            if (from == 0) return "1=1";
            return $"EXISTS (SELECT 1 FROM Visits WHERE CustomerId = c.Id AND DATEPART(HOUR, VisitDateTime) BETWEEN {from} AND {to})";
        }

        /// Loyalty segments (nodes 5503-5509)
        private static string BuildLoyaltySegmentCondition(string nodeName) => nodeName switch
        {
            "Loyal" => "((SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id AND DATEDIFF(MONTH, VisitDateTime, GETDATE()) <= 12) >= 10 OR EXISTS (SELECT 1 FROM Visits WHERE CustomerId = c.Id AND IsLoyalty = 1))",
            "Frequent" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id AND DATEDIFF(MONTH, VisitDateTime, GETDATE()) <= 12) BETWEEN 5 AND 9",
            "Occasional" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id AND DATEDIFF(MONTH, VisitDateTime, GETDATE()) <= 12) BETWEEN 2 AND 4",
            "Infrequent" => "(SELECT COUNT(*) FROM Visits WHERE CustomerId = c.Id AND DATEDIFF(MONTH, VisitDateTime, GETDATE()) <= 12) = 1",
            "Lapsed" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) BETWEEN DATEADD(MONTH,-12,GETDATE()) AND DATEADD(MONTH,-6,GETDATE())",
            "Long-term lapsed" => "(SELECT MAX(VisitDateTime) FROM Visits WHERE CustomerId = c.Id) < DATEADD(MONTH,-12,GETDATE())",
            "Never" => "NOT EXISTS (SELECT 1 FROM Visits WHERE CustomerId = c.Id)",
            _ => "1=1"
        };

        /// Site preference (nodes 5461-5463, 5510, 5776, 5777)
        /// rule.Value must carry the actual SiteId integer chosen by the user in the UI.
        /// Note: TOP 1 with GROUP BY inside a scalar subquery compared with = is valid in SQL Server,
        /// but we use EXISTS with a ranked subquery to be safe and avoid edge cases.
        private static string BuildSiteIdCondition(TreeNode node, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "1=1";

            string nodeName = node.NodeName?.ToLower() ?? "";
            string siteId = SafeNumber(value);

            if (nodeName.Contains("home") || nodeName.Contains("favourite") || nodeName.Contains("preferred"))
                // Most visited site = the one with the highest visit count
                return $"EXISTS (" +
                       $"SELECT 1 FROM (" +
                       $"SELECT TOP 1 SiteId FROM Visits WHERE CustomerId = c.Id " +
                       $"GROUP BY SiteId ORDER BY COUNT(*) DESC" +
                       $") AS _top WHERE _top.SiteId = {siteId})";

            if (nodeName.Contains("last"))
                // Most recent site visited
                return $"EXISTS (" +
                       $"SELECT 1 FROM (" +
                       $"SELECT TOP 1 SiteId FROM Visits WHERE CustomerId = c.Id " +
                       $"ORDER BY VisitDateTime DESC" +
                       $") AS _last WHERE _last.SiteId = {siteId})";

            // "Has Visited" / "Has Interacted With"
            return $"EXISTS (SELECT 1 FROM Visits WHERE CustomerId = c.Id AND SiteId = {siteId})";
        }

        /// Boolean (node 209 Surveys, 5465/5466 Consent, 5469-5472 Data mgmt, 5786 Gift Card)
        private string BuildBooleanCondition(TreeNode node, string value)
        {
            string field = GetQualifiedField(node);
            bool boolVal = value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
            return $"{field} = {(boolVal ? 1 : 0)}";
        }

        // ─────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────

        private string GetQualifiedField(TreeNode node)
        {
            var table = NormaliseEntity(node.EntityName);
            var alias = TableAliases.TryGetValue(table, out var a) ? a : "c";
            return $"{alias}.{node.FieldName}";
        }

        /// Build LEFT JOINs for every table that is actually needed.
        /// Visits must come before Sites (Sites JOIN references alias v).
        private static string BuildJoinClause(HashSet<string> usedTables)
        {
            var joins = new List<string>();

            if (usedTables.Contains("CustomerAddresses"))
                joins.Add("LEFT JOIN CustomerAddresses a ON a.CustomerId = c.Id");

            if (usedTables.Contains("Transactions"))
                joins.Add("LEFT JOIN Transactions t ON t.CustomerId = c.Id");

            // Visits must be added before Sites
            bool needsVisits = usedTables.Contains("Visits") || usedTables.Contains("Sites");
            if (needsVisits)
                joins.Add("LEFT JOIN Visits v ON v.CustomerId = c.Id");

            if (usedTables.Contains("Bookings"))
                joins.Add("LEFT JOIN Bookings b ON b.CustomerId = c.Id");

            if (usedTables.Contains("Sites"))
                joins.Add("LEFT JOIN Sites s ON s.Id = v.SiteId");

            return string.Join(" ", joins);
        }

        private static string Escape(string value) => value.Replace("'", "''");

        private static bool IsValidFieldName(string? field) =>
            !string.IsNullOrWhiteSpace(field) &&
            field.All(c => char.IsLetterOrDigit(c) || c == '_');

        private static string SafeNumber(string value)
        {
            value = value.Trim();
            if (decimal.TryParse(value,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var n))
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);

            throw new ArgumentException($"Invalid numeric value: '{value}'");
        }

        private static List<string> GetRegionCities(string regionName) => regionName switch
        {
            "East" => new() { "Cambridge", "Ipswich", "Norwich", "Peterborough" },
            "London" => new() { "London E", "London EC", "London N", "London NW", "London SE", "London SW", "London W", "London WC" },
            "Midlands" => new() { "Birmingham", "Coventry", "Derby", "Dudley", "Hereford", "Leicester", "Lincoln", "Nottingham", "Northampton", "Stoke-On-Trent", "Shrewsbury", "Telford", "Worcester", "Walsall", "Wolverhampton" },
            "N.Ireland" => new() { "Belfast" },
            "North East" => new() { "Bradford", "Durham", "Darlington", "Doncaster", "Huddersfield", "Harrogate", "Hull", "Halifax", "Leeds", "Newcastle Upon Tyne", "Sheffield", "Sunderland", "Cleveland", "Wakefield", "York" },
            "North West" => new() { "Blackburn", "Bolton", "Carlisle", "Chester", "Crewe", "Blackpool", "Liverpool", "Lancaster", "Manchester", "Oldham", "Preston", "Stockport", "Warrington", "Wigan" },
            "Scotland" => new() { "Aberdeen", "Dundee", "Dumfries and Galloway", "Edinburgh", "Falkirk and Stirling", "Glasgow", "Outer Hebrides", "Inverness", "Kilmarnock", "Kirkwall", "Kirkcaldy", "Motherwell", "Paisley", "Perth", "Galashiels", "Shetland" },
            "South East" => new() { "St. Albans", "Brighton", "Bromley", "Chelmsford", "Colchester", "Croydon", "Canterbury", "Dartford", "Enfield", "Harrow", "Hemel Hempstead", "Ilford", "Kingston upon Thames", "Luton", "Rochester", "Milton Keynes", "Oxford", "Portsmouth", "Reading", "Redhill", "Romford", "Stevenage", "Slough", "Sutton", "Southampton", "Southend-on-Sea", "Tonbridge", "Twickenham", "Southall", "Watford" },
            "South West" => new() { "Bath", "Bournemouth", "Bristol", "Dorchester", "Exeter", "Gloucester", "Guildford", "Plymouth", "Swindon", "Salisbury", "Taunton", "Newton Abbot", "Truro", "Torquay" },
            "Wales" => new() { "Cardiff", "Brecon", "Llandudno", "Newport", "Swansea" },
            _ => new()
        };
    }
}