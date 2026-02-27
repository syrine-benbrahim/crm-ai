using crm_ai.DTOs;

namespace crm_ai.Services.Interfaces
{
    public interface ITreeService
    {
        Task<List<TreeNodeDto>> GetTreeAsync();
    }
}