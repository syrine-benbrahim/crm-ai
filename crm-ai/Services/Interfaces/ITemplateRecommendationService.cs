namespace crm_ai.Services.Interfaces
{
    using crm_ai.DTOs;

    public interface ITemplateRecommendationService
    {
        /// <summary>
        /// Recommends a template using pure C# signal detection.
        /// Zero AI tokens. Deterministic. Loads templates from templates.json
        /// via ITemplateRenderingService — single source of truth.
        /// </summary>
        Task<TemplateRecommendationResultDto> RecommendAsync(
            string objective,
            string channel,
            string? selectionDescription);
    }
}