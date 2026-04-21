using crm_ai.DTOs;

namespace crm_ai.Services.Interfaces
{
    public interface ISelectionService
    {
        Task<int> CreateSelection(SelectionRequestDto dto);
        Task<object> PreviewSelection(SelectionRequestDto dto);
        Task<object> ExecuteSelection(int id);
        Task<List<object>> GetExecutions(int selectionId);
        Task<List<object>> GetAllSelections();
        Task<object> GetSelectionById(int id);
        Task DeleteSelection(int id);                         
        Task UpdateSelection(int id, SelectionRequestDto dto);
        Task<int> DuplicateSelection(int id);
        Task BulkDeleteSelections(List<int> ids);
        Task BulkArchiveSelections(List<int> ids);
        Task ArchiveSelection(int id);
    }
}