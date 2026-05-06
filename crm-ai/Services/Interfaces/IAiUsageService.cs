namespace crm_ai.Services.Interfaces
{
    public interface IAiUsageService
    {
        void Record(
            string model,
            string feature,
            int tokensUsed,
            int maxTokens,
            bool success,
            string? errorMessage = null);
    }
}