using crm_ai.Models;

namespace crm_ai.Services
{
    public interface ITemplateRenderingService
    {
        Task<List<TemplateSchema>> GetAllTemplatesAsync();
        Task<TemplateSchema?> GetTemplateAsync(string id);
        Task<TemplateSchema?> RecommendAsync(string campaignType, string tone, string channel);
        Task<RenderResult> RenderAsync(string templateId, Dictionary<string, string> slots);
    }
}