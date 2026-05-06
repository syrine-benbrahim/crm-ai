namespace crm_ai.DTOs
{
    // ── Response from RecommendAsync ─────────────────────────────────────
    public class TemplateRecommendationResultDto
    {
        /// <summary>
        /// The recommended template ID. Null when RequiresManualBuild = true
        /// or when no confident match was found.
        /// </summary>
        public string? RecommendedTemplateId { get; set; }

        /// <summary>Detected campaign type: winback, retention, conversion, upsell, reactivation</summary>
        public string CampaignType { get; set; } = "";

        /// <summary>Detected tone: urgent, friendly, premium, promotional</summary>
        public string Tone { get; set; } = "";

        /// <summary>
        /// Human-readable explanation of the recommendation.
        /// Built deterministically by C#. Never AI-generated.
        /// e.g. "Lapsed/inactive audience detected; lapsed customers need urgency to re-engage"
        /// </summary>
        public string? RecommendationReason { get; set; }

        /// <summary>All templates for this channel, recommended first.</summary>
        public List<TemplateSuggestionDto> Templates { get; set; } = [];

        /// <summary>
        /// True when no channel-compatible templates exist, or when
        /// the top score is below the confidence threshold.
        /// When true: show the manual content builder instead of template cards.
        /// </summary>
        public bool RequiresManualBuild { get; set; }

        /// <summary>
        /// Explains why manual build is shown.
        /// Only populated when RequiresManualBuild = true.
        /// e.g. "No SMS templates are available. You can build content manually below."
        /// </summary>
        public string? ManualBuildReason { get; set; }
    }

    public class TemplateSuggestionDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string? PreviewImageUrl { get; set; }
        public string[] SupportedObjectives { get; set; } = [];
        public string[] SupportedTones { get; set; } = [];
        public string[] ContentSlots { get; set; } = [];
        public bool IsRecommended { get; set; }

        /// <summary>Only populated when IsRecommended = true.</summary>
        public string? RecommendationReason { get; set; }
    }

    // ── Request for standalone endpoint ──────────────────────────────────
    public class RecommendTemplateRequestDto
    {
        public string Objective { get; set; } = "";
        public string Channel { get; set; } = "";
        public string? SelectionDescription { get; set; }
    }
}