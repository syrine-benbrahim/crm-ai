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
        Task<CampaignStrategyDto> GenerateStrategyAsync(int campaignId, GenerateStrategyRequestDto request);
        Task<CampaignExecutionResultDto> ExecuteCampaignAsync(int campaignId);
        Task<CampaignSimulationDto> SimulateCampaignAsync(int campaignId);
        Task<SelectionSuggestionResultDto> SuggestSelectionsAsync(SuggestSelectionsRequestDto request); // ← add this
    }
}