using crm_ai.DTOs;

namespace crm_ai.Services.Interfaces
{
    public interface ICampaignService
    {
        Task<CampaignConversationResponseDto> ContinueCampaignConversationAsync(
            CampaignConversationRequestDto request);

        Task<List<CampaignSummaryDto>> GetAllCampaignsAsync();
        Task<CampaignSummaryDto> GetCampaignByIdAsync(int id);
        Task LinkSelectionAsync(int campaignId, int selectionId);
        Task DeleteCampaignAsync(int id);
    }
}