namespace crm_ai.DTOs
{
    // ── Request — frontend sends this every turn ──────────────────────────
    public class WizardRequestDto
    {
        public List<ConversationMessage> Messages { get; set; } = [];
        public WizardStateDto State { get; set; } = new();
    }

    // ── The full state the frontend owns and sends back each turn ─────────
    public class WizardStateDto
    {
        public string Phase { get; set; } = "collecting";
        // collecting | suggest_selection | building_selection |
        // strategy | suggest_template | generating_content |
        // suggest_schedule | confirming | completed

        public CampaignDraftDto CampaignDraft { get; set; } = new();

        // Selection fields
        public int? SelectionId { get; set; }
        public string? SelectionName { get; set; }
        public string? SelectionDescription { get; set; }
        public SelectionGroupDto? SelectionRootGroup { get; set; }

        // Strategy (set after phase 4)
        public string? CampaignType { get; set; }
        public string? Tone { get; set; }
        public SegmentProfileDto? SegmentProfile { get; set; }
        public List<StrategyExplanationPointDto> StrategyExplanation { get; set; } = [];

        // Template (set after phase 5)
        public string? ChosenTemplateId { get; set; }

        // Content (set after phase 6)
        public WizardContentDto? GeneratedContent { get; set; }

        // Schedule (set after phase 7)
        public string? ScheduledAt { get; set; }

        // Selection builder sub-state (while building_selection)
        public bool? SelectionIntentConfirmed { get; set; }
        public SelectionGroupDto? SelectionCurrentRootGroup { get; set; }
    }

    // ── Response — what the backend sends back each turn ──────────────────
    public class WizardResponseDto
    {
        public string Phase { get; set; } = "";
        public string Message { get; set; } = "";

        // Phase-specific payloads
        public List<WizardSelectionSuggestionDto> SelectionSuggestions { get; set; } = [];
        public List<TemplateMetadataDto> Templates { get; set; } = [];
        public string? RecommendedTemplateId { get; set; }
        public WizardContentDto? Content { get; set; }
        public SendTimeRecommendationDto? SendTime { get; set; }
        public List<StrategyExplanationPointDto> StrategyExplanation { get; set; } = [];
        public SegmentProfileDto? SegmentProfile { get; set; }

        // Clarification cards (during building_selection)
        public List<ClarificationBlockDto> Clarifications { get; set; } = [];
        public string? ClarificationStateId { get; set; }

        // Updated state — frontend stores this and sends it next turn
        public WizardStateDto State { get; set; } = new();
        public int TokensUsed { get; set; }
        public bool RequiresManualBuild { get; set; }
    }

    public class WizardSelectionSuggestionDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int CustomerCount { get; set; }
        public bool IsRecommended { get; set; }
        public string? RecommendationReason { get; set; }
    }

    public class WizardContentDto
    {
        public string Subject { get; set; } = "";
        public string Preheader { get; set; } = "";
        public string HeroHeadline { get; set; } = "";
        public string BodyPara1 { get; set; } = "";
        public string BodyPara2 { get; set; } = "";
        public string CtaText { get; set; } = "";
        public string SmsText { get; set; } = "";
        public string FinalHtml { get; set; } = "";
    }
}