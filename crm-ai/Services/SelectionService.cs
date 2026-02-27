using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Models;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace crm_ai.Services
{
    public class SelectionService : ISelectionService
    {
        private readonly AppDbContext _context;
        private readonly ISqlBuilderService _sqlBuilder;

        public SelectionService(AppDbContext context, ISqlBuilderService sqlBuilder)
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
                UpdatedAt = DateTime.UtcNow,  
                Status = "Active",             
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

            var customers = await _context.Customers
                .FromSqlRaw(query)
                .ToListAsync();

            var emails = customers.Select(c => c.Email).ToList();

            var execution = new SelectionExecution
            {
                SelectionId = selection.Id,
                ExecutedAt = DateTime.UtcNow,
                TotalUsers = customers.Count,
                EmailsJson = System.Text.Json.JsonSerializer.Serialize(emails)
            };

            _context.SelectionExecutions.Add(execution);
            await _context.SaveChangesAsync();

            return new
            {
                ExecutionId = execution.Id,
                TotalUsers = execution.TotalUsers,
                Emails = emails
            };
        }

        public async Task<List<object>> GetExecutions(int selectionId)
        {
            var executions = await _context.SelectionExecutions
                .Where(e => e.SelectionId == selectionId)
                .OrderByDescending(e => e.ExecutedAt)
                .ToListAsync();

            return executions.Select(e => (object)new
            {
                e.Id,
                e.ExecutedAt,
                e.TotalUsers,
                Emails = string.IsNullOrEmpty(e.EmailsJson)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(e.EmailsJson)
            }).ToList();
        }

        public async Task<List<object>> GetAllSelections()
        {
            return await _context.Selections
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => (object)new
                {
                    s.Id,
                    s.Name,
                    s.CreatedAt,
                    s.UpdatedAt,   
                    s.Status,
                    RuleCount = s.Groups.SelectMany(g => g.Rules).Count()
                })
                .ToListAsync();
        }

        public async Task<object> GetSelectionById(int id)
        {
            var selection = await _context.Selections
                .Include(s => s.Groups)
                    .ThenInclude(g => g.Rules)
                .Include(s => s.Groups)
                    .ThenInclude(g => g.ChildGroups)
                        .ThenInclude(g => g.Rules)
                .AsSplitQuery()
                .FirstOrDefaultAsync(s => s.Id == id);

            if (selection == null)
                throw new Exception("Selection not found");

            var rootGroup = selection.Groups
                .FirstOrDefault(g => g.ParentGroupId == null);

            return new
            {
                selection.Id,
                selection.Name,
                selection.CreatedAt,
                selection.UpdatedAt,   
                selection.Status,
                RootGroup = MapToDto(rootGroup)
            };
        }

        public async Task DeleteSelection(int id)
        {
            var selection = await _context.Selections
                .Include(s => s.Groups)
                    .ThenInclude(g => g.Rules)
                .Include(s => s.Groups)
                    .ThenInclude(g => g.ChildGroups)
                        .ThenInclude(g => g.Rules)
                .Include(s => s.SelectionExecutions)
                .FirstOrDefaultAsync(s => s.Id == id);

            if (selection == null)
                throw new Exception("Selection not found");

            _context.Selections.Remove(selection);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateSelection(int id, SelectionRequestDto dto)
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

            // Remove all old groups and rules
            _context.SelectionGroups.RemoveRange(selection.Groups);
            await _context.SaveChangesAsync();

            // Rebuild from new dto
            if (!string.IsNullOrWhiteSpace(dto.Name))
                selection.Name = dto.Name;

            if (!string.IsNullOrWhiteSpace(dto.Name))
                selection.Name = dto.Name;

            selection.UpdatedAt = DateTime.UtcNow;

            var rootGroup = BuildGroup(dto.RootGroup, null, selection);
            selection.Groups = new List<SelectionGroup> { rootGroup };

            await _context.SaveChangesAsync();
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

            if (dto.Groups != null)
            {
                foreach (var child in dto.Groups)
                {
                    group.ChildGroups.Add(BuildGroup(child, group, selection));
                }
            }

            return group;
        }

        public SelectionGroupDto MapToDto(SelectionGroup group)
        {
            if (group == null) return null;

            return new SelectionGroupDto
            {
                LogicalOperator = group.LogicalOperator,
                Rules = group.Rules?
                    .Select(r => new SelectionRuleDto
                    {
                        TreeNodeId = r.TreeNodeId,
                        Operator = r.Operator,
                        Value = r.Value
                    }).ToList() ?? new List<SelectionRuleDto>(),
                Groups = group.ChildGroups?
                    .Select(child => MapToDto(child))
                    .ToList() ?? new List<SelectionGroupDto>()
            };
        }
    }
}