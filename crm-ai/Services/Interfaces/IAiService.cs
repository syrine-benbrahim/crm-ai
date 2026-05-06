using crm_ai.DTOs;

namespace crm_ai.Services.Interfaces
{
    public interface IAiService
    {
        Task<AiDescriptionResponseDto> GenerateSelectionDescriptionAsync(
            SelectionGroupDto rootGroup);

        Task<AiSelectionResponseDto> GenerateSelectionFromPromptAsync(
            string prompt,
            string? name = null);

        Task<AiValidationResponseDto> ValidateSelectionAsync(
            SelectionGroupDto rootGroup);

        Task<ConversationResponseDto> ContinueConversationAsync(
            ConversationRequestDto request);

        Task<IntentCheckResponseDto> CheckIntentAsync(IntentCheckRequestDto request);

        Task<AiDescriptionResponseDto> GenerateDescriptionOnDemandAsync(
            SelectionGroupDto rootGroup);

        Task<string> GenerateNameOnDemandAsync(string description);

        Task<(int Score, int Tokens)> ScoreConfidenceOnDemandAsync(
            string intent, SelectionGroupDto rootGroup);

        Task<(string Response, int TokensUsed)> CallPublicAsync(
            string systemPrompt, string userPrompt, int maxTokens = 200,
            string? model = null);

        Task<Dictionary<int, NodeCatalogItem>> BuildNodeCatalogPublicAsync();

        string FastModel { get; }
        string PowerModel { get; }
    }
}