namespace crm_ai.DTOs
{
    /// <summary>
    /// Request: user sends a plain-English prompt describing their audience.
    /// </summary>
    public class AiSelectionRequestDto
    {
        /// <summary>
        /// Plain-English audience description.
        /// Example: "Female customers in London aged 25-34 who visited last week"
        /// </summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// Optional name for the selection.
        /// If not provided, the AI will generate one.
        /// </summary>
        public string? Name { get; set; }
    }
}