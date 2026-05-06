namespace crm_ai.Services.Interfaces
{
    using crm_ai.DTOs;

    public interface ISelectionSuggestionService
    {
        Task<SelectionSuggestionResultDto> SuggestSelectionsAsync(
            string objective,
            string channel,
            string? campaignName = null,
            int maxResults = 5);
    }
}