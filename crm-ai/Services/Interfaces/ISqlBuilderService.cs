using crm_ai.DTOs;

namespace crm_ai.Services.Interfaces
{
    public interface ISqlBuilderService
    {
        Task<(string WhereClause, string JoinClause)> BuildQueryPartsAsync(SelectionGroupDto group);
    }
}