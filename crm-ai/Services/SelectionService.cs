using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace crm_ai.Services
{
    public class SelectionService
    {
        private readonly AppDbContext _context;
        private readonly SqlBuilderService _sqlBuilder;

        public SelectionService(
            AppDbContext context,
            SqlBuilderService sqlBuilder)
        {
            _context = context;
            _sqlBuilder = sqlBuilder;
        }

        public async Task<int> CreateSelection(SelectionRequestDto dto)
        {
            var selection = new Selection
            {
                Name = dto.Name,
                CreatedAt = DateTime.UtcNow,
                Groups = new List<SelectionGroup>()
            };

            var rootGroup = BuildGroup(dto.RootGroup, null, selection);
            selection.Groups.Add(rootGroup);

            _context.Selections.Add(selection);
            await _context.SaveChangesAsync();

            return selection.Id;
        }

        public async Task<object> PreviewSelection(SelectionRequestDto dto)
        {
            if (dto?.RootGroup == null)
                throw new ArgumentException("Root group is required");

            var (whereClause, joinClause) =
                await _sqlBuilder.BuildQueryPartsAsync(dto.RootGroup);

            if (string.IsNullOrWhiteSpace(whereClause))
                whereClause = "1=1";

            var query = $@"
        SELECT DISTINCT c.*
        FROM Customers c
        {joinClause}
        WHERE {whereClause}";

            Console.WriteLine("===== PREVIEW QUERY =====");
            Console.WriteLine(query);

            var customers = await _context.Customers
                .FromSqlRaw(query)
                .ToListAsync();

            return new
            {
                TotalCount = customers.Count,
                SampleEmails = customers.Take(20).Select(c => c.Email),
                WhereClause = whereClause
            };
        }

        private SelectionGroup BuildGroup(
            SelectionGroupDto dto,
            SelectionGroup? parent,
            Selection selection)
        {
            var group = new SelectionGroup
            {
                Selection = selection,
                ParentGroup = parent,
                LogicalOperator = dto.LogicalOperator,
                Rules = new List<SelectionRule>(),
                ChildGroups = new List<SelectionGroup>()
            };

            // Rules
            if (dto.Rules != null)
            {
                foreach (var ruleDto in dto.Rules)
                {
                    group.Rules.Add(new SelectionRule
                    {
                        TreeNodeId = ruleDto.TreeNodeId,
                        Operator = ruleDto.Operator,
                        Value = ruleDto.Value
                    });
                }
            }

            // Child Groups (recursive)
            if (dto.Groups != null)
            {
                foreach (var child in dto.Groups)
                {
                    group.ChildGroups.Add(
                        BuildGroup(child, group, selection));
                }
            }

            return group;
        }
        private async Task<string> BuildGroupSql(SelectionGroup group)
        {
            var conditions = new List<string>();

            // Load TreeNodes for rules
            foreach (var rule in group.Rules)
            {
                var node = await _context.TreeNodes
                    .FirstOrDefaultAsync(t => t.Id == rule.TreeNodeId);

                if (node == null || string.IsNullOrEmpty(node.FieldName))
                    continue;

                conditions.Add(BuildCondition(node, rule));
            }

            // Load child groups recursively
            var childGroups = await _context.SelectionGroups
                .Where(g => g.ParentGroupId == group.Id)
                .Include(g => g.Rules)
                .ToListAsync();

            foreach (var child in childGroups)
            {
                var childSql = await BuildGroupSql(child);
                if (!string.IsNullOrWhiteSpace(childSql))
                    conditions.Add($"({childSql})");
            }

            if (!conditions.Any())
                return "1=1";

            return string.Join($" {group.LogicalOperator} ", conditions);
        }
        public async Task<object> ExecuteSelection(int id)
        {
            var selection = await _context.Selections
                .Include(s => s.Groups)
                    .ThenInclude(g => g.Rules)
                .Include(s => s.Groups)
                    .ThenInclude(g => g.ChildGroups)
                        .ThenInclude(g => g.Rules)
                .FirstOrDefaultAsync(s => s.Id == id);

            if (selection == null)
                throw new Exception("Selection not found");

            var rootGroup = selection.Groups
                .FirstOrDefault(g => g.ParentGroupId == null);

            if (rootGroup == null)
                throw new Exception("Root group not found");

            var dto = MapToDto(rootGroup);

            var (whereClause, joinClause) =
                await _sqlBuilder.BuildQueryPartsAsync(dto);

            if (string.IsNullOrWhiteSpace(whereClause))
                whereClause = "1=1";

            var query = $@"
        SELECT DISTINCT c.*
        FROM Customers c
        {joinClause}
        WHERE {whereClause}";

            Console.WriteLine("===== EXECUTE QUERY =====");
            Console.WriteLine(query);

            var customers = await _context.Customers
                .FromSqlRaw(query)
                .ToListAsync();

            // 🔥 Save execution
            var execution = new SelectionExecution
            {
                SelectionId = selection.Id,
                ExecutedAt = DateTime.UtcNow,
                TotalUsers = customers.Count,
                Users = customers.Select(c => new SelectionExecutionUser
                {
                    CustomerId = c.Id
                }).ToList()
            };

            _context.SelectionExecutions.Add(execution);
            await _context.SaveChangesAsync();

            return new
            {
                ExecutionId = execution.Id,
                TotalUsers = execution.TotalUsers,
                Emails = customers.Select(c => c.Email)
            };
        }

        public async Task<List<object>> GetExecutions(int selectionId)
        {
            return await _context.SelectionExecutions
                .Where(e => e.SelectionId == selectionId)
                .Select(e => (object)new
                {
                    e.Id,
                    e.ExecutedAt,
                    e.TotalUsers
                })
                .ToListAsync();
        }
        private string BuildCondition(TreeNode node, SelectionRule rule)
        {
            var field = node.FieldName;
            var dataType = node.DataType?.ToLower() ?? "string";
            var value = rule.Value;

            // Handle 18-24 range
            if (dataType == "number" && value.Contains("-"))
            {
                var parts = value.Split('-');
                if (int.TryParse(parts[0], out int min) &&
                    int.TryParse(parts[1], out int max))
                {
                    return $"{field} BETWEEN {min} AND {max}";
                }
            }

            if (dataType == "number")
                return $"{field} {rule.Operator} {value}";

            return $"{field} = '{value}'";
        }
        private SelectionGroupDto MapToDto(SelectionGroup group)
        {
            return new SelectionGroupDto
            {
                LogicalOperator = group.LogicalOperator,
                Rules = group.Rules?.Select(r => new SelectionRuleDto
                {
                    TreeNodeId = r.TreeNodeId,
                    Operator = r.Operator,
                    Value = r.Value
                }).ToList(),

                Groups = group.ChildGroups?
                    .Select(child => MapToDto(child))
                    .ToList()
            };
        }


    }
}
