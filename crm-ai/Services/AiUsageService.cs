using crm_ai.Data;
using crm_ai.Models;
using crm_ai.Services.Interfaces;

namespace crm_ai.Services
{
    /// <summary>
    /// Writes one record per Groq call to the database.
    /// Intentionally fire-and-forget — never throws, never blocks the caller.
    /// </summary>
    public class AiUsageService : IAiUsageService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AiUsageService> _logger;

        public AiUsageService(
            IServiceScopeFactory scopeFactory,
            ILogger<AiUsageService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public void Record(
            string model,
            string feature,
            int tokensUsed,
            int maxTokens,
            bool success,
            string? errorMessage = null)
        {
            // Fire and forget — run in background, never block the AI call
            _ = Task.Run(async () =>
            {
                try
                {
                    // AiService is scoped, so we need our own scope here
                    using var scope = _scopeFactory.CreateScope();
                    var context = scope.ServiceProvider
                        .GetRequiredService<AppDbContext>();

                    context.AiUsageRecords.Add(new AiUsageRecord
                    {
                        Model = model,
                        Feature = feature,
                        TokensUsed = tokensUsed,
                        MaxTokens = maxTokens,
                        Success = success,
                        ErrorMessage = errorMessage,
                        CalledAt = DateTime.UtcNow
                    });

                    await context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    // Never let audit logging crash the main flow
                    _logger.LogError(ex,
                        "Failed to write AI usage record — " +
                        "model={Model}, tokens={Tokens}",
                        model, tokensUsed);
                }
            });
        }
    }
}