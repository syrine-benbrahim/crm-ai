namespace crm_ai.DTOs
{
    /// <summary>
    /// Request: user sends their manually built rule tree for AI validation.
    /// </summary>
    public class AiValidationRequestDto
    {
        public SelectionGroupDto RootGroup { get; set; } = new();
    }

    /// <summary>
    /// Response: AI analysis of the rule tree — what it does and any issues found.
    /// </summary>
    public class AiValidationResponseDto
    {
        /// <summary>
        /// Plain-English explanation of what the selection actually targets.
        /// e.g. "Your selection targets female customers aged 25-34 in London."
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Overall validation result: "valid", "warning", "error"
        /// </summary>
        public string Status { get; set; } = "valid";

        /// <summary>
        /// List of logical issues or suggestions found by the AI.
        /// Empty = no issues found.
        /// </summary>
        public List<ValidationIssue> Issues { get; set; } = new();

        /// <summary>
        /// True if the selection looks correct and ready to use.
        /// </summary>
        public bool IsValid => Status != "error";

        public int TokensUsed { get; set; }
    }

    public class ValidationIssue
    {
        /// <summary>
        /// "warning" or "error"
        /// </summary>
        public string Severity { get; set; } = "warning";

        /// <summary>
        /// Short title of the issue.
        /// e.g. "Conflicting age ranges"
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Full explanation and suggestion.
        /// e.g. "You have age ranges in an AND group which means a customer 
        /// must be both 18-24 AND 25-34, which is impossible. Use OR instead."
        /// </summary>
        public string Detail { get; set; } = string.Empty;
    }
}