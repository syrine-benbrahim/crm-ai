namespace crm_ai.Models
{
    public class AiUsageRecord
    {
        public int Id { get; set; }
        public string Model { get; set; } = "";
        public string Feature { get; set; } = ""; // e.g. "build", "clarify", "describe"
        public int TokensUsed { get; set; }
        public int MaxTokens { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CalledAt { get; set; } = DateTime.UtcNow;
    }
}