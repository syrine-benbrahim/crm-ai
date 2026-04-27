namespace crm_ai.Helpers
{
    public sealed class GroqResilienceOptions
    {
        public int FailuresBeforeBreaking { get; set; } = 5;
        public int BreakDurationSeconds { get; set; } = 30;
        public int TimeoutSeconds { get; set; } = 15;
        public int RetryCount { get; set; } = 3;
    }
}