using crm_ai.DTOs;

namespace crm_ai.DTOs
{
    /// <summary>
    /// Response: AI-generated selection ready to preview or save.
    /// </summary>
    public class AiSelectionResponseDto
    {
        /// <summary>
        /// AI-suggested name for the selection.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// AI-generated plain-English description of the audience.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The fully built rule tree with real TreeNode IDs.
        /// Ready to pass directly to POST /api/selection or POST /api/selection/preview.
        /// </summary>
        public SelectionGroupDto RootGroup { get; set; } = new();

        /// <summary>
        /// Confidence score 0-100 from the AI validation step.
        /// >= 80 = high confidence, 50-79 = medium, below 50 = low
        /// </summary>
        public int Confidence { get; set; }

        /// <summary>
        /// Confidence label: "High", "Medium", "Low"
        /// </summary>
        public string ConfidenceLabel => Confidence >= 80 ? "High"
                                       : Confidence >= 50 ? "Medium"
                                       : "Low";

        /// <summary>
        /// Terms in the user prompt that the AI could not map to any TreeNode.
        /// Empty = everything was matched successfully.
        /// </summary>
        public List<string> UnmatchedTerms { get; set; } = new();

        /// <summary>
        /// Total tokens used across all AI calls for this request.
        /// </summary>
        public int TokensUsed { get; set; }
    }
}