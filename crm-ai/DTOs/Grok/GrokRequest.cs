using System.Text.Json.Serialization;

namespace crm_ai.DTOs.Grok
{
    public class GrokRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<GrokMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")]
        public float Temperature { get; set; } = 0.3f;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 300;
    }

    public class GrokMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;   // "system" | "user"

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}