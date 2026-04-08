namespace crm_ai.DTOs
{
    public class AiDescriptionResponseDto
    {
        public string Description { get; set; } = string.Empty;
        public int TokensUsed { get; set; }
        public bool FromCache { get; set; }
    }
}