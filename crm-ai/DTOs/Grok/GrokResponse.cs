using System.Text.Json.Serialization;

namespace crm_ai.DTOs.Grok
{
    public class GrokResponse
    {
        [JsonPropertyName("choices")]
        public List<GrokChoice> Choices { get; set; } = new();

        [JsonPropertyName("usage")]
        public GrokUsage? Usage { get; set; }
    }

    public class GrokChoice
    {
        [JsonPropertyName("message")]
        public GrokMessage Message { get; set; } = new();
    }

    public class GrokUsage
    {
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}