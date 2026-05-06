namespace crm_ai.DTOs
{
    // ── The draft that gets built up turn by turn ─────────────────────────
    public class CampaignDraftDto
    {
        public int? Id { get; set; }
        public string? Name { get; set; }
        public string? Objective { get; set; }
        public string? Channel { get; set; }  // "Email" | "SMS" | null
        public int? SelectionId { get; set; }
        public string? SelectionName { get; set; }
    }

    // ── What the frontend sends every turn ───────────────────────────────
    public class CampaignConversationRequestDto
    {
        public List<ConversationMessage> Messages { get; set; } = new();
        public CampaignDraftDto? CurrentDraft { get; set; }
        public bool Confirmed { get; set; }
    }

    // ── What the backend returns every turn ──────────────────────────────
    public class CampaignConversationResponseDto
    {
        public string Status { get; set; } = "";
        // "collecting"  — still gathering info, showing a question
        // "confirming"  — all fields collected, asking user to confirm
        // "completed"   — saved to DB, ready for content generation
        // "error"       — something went wrong

        public string Message { get; set; } = "";
        public CampaignDraftDto Draft { get; set; } = new();
        public List<string> MissingFields { get; set; } = new();
        public int TokensUsed { get; set; }
        public List<string> SuggestedNames { get; set; } = new();
        public SelectionSuggestionResultDto? SelectionSuggestion { get; set; }
    }

    // ── CRUD DTOs ─────────────────────────────────────────────────────────
    public class CampaignSummaryDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Objective { get; set; }
        public string Channel { get; set; } = "";
        public string Status { get; set; } = "";
        public string? SelectionName { get; set; }
        public bool HasContent { get; set; }
        public bool HasSchedule { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int? SelectionId { get; set; }
    }

    public class LinkSelectionDto
    {
        public int SelectionId { get; set; }
    }

    public class SegmentProfileDto
    {
        public int AudienceSize { get; set; }
        public float EmailCoveragePercent { get; set; }
        public float SmsCoveragePercent { get; set; }
        public string DominantRecency { get; set; } = "";
        public string DominantLoyaltyTier { get; set; } = "";
        public string DominantSpendTier { get; set; } = "";
        public string[] DominantLocations { get; set; } = [];
        public string[] AgeRanges { get; set; } = [];
        public string? Gender { get; set; }
        public string EngagementLevel { get; set; } = "";
        // "Active" | "AtRisk" | "Lapsed" | "LongTermLapsed" | "Unknown"
        public string ValueTier { get; set; } = "";
        // "High" | "Medium" | "Low" | "Unknown"
        public string BehaviourSummary { get; set; } = "";
        public string SelectionDescription { get; set; } = "";
        public string? RecommendedSendDay { get; set; }
        public string? RecommendedSendHour { get; set; }
        public List<VisitPatternDto> TopVisitPatterns { get; set; } = [];
    }

    public class VisitPatternDto
    {
        public string Day { get; set; } = "";
        public string Hour { get; set; } = "";
        public int VisitCount { get; set; }
        public float RelativeStrength { get; set; }
    }
    public class GenerateStrategyRequestDto
    {
        public SelectionGroupDto RootGroup { get; set; } = null!;
        public string SelectionDescription { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Objective { get; set; } = "";
    }

    public class CampaignStrategyDto
    {
        public string CampaignType { get; set; } = "";
        public string Tone { get; set; } = "";
        public string? RecommendedSendTime { get; set; }
        public List<StrategyExplanationPointDto> Explanation { get; set; } = [];
        public DecisionFlowDto DecisionFlow { get; set; } = new();  // ← ADD
        public SegmentProfileDto SegmentProfile { get; set; } = null!;
        public int TokensUsed { get; set; }
    }

    public class StrategyExplanationPointDto
    {
        public string Signal { get; set; } = "";
        // The data fact: "67% of audience inactive 3+ months"

        public string Implication { get; set; } = "";
        // The reasoning: "Lapsed customers respond to urgency"

        public string Decision { get; set; } = "";
        // The choice: "Tone set to urgent"
    }
    public class TemplateMetadataDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string PreviewImageUrl { get; set; } = "";
        public string[] SupportedObjectives { get; set; } = [];
        public string[] SupportedTones { get; set; } = [];
        public string[] SupportedChannels { get; set; } = [];
        public string[] ContentSlots { get; set; } = [];
        public bool IsRecommended { get; set; }
        public string? RecommendationReason { get; set; }
    }

    public class SendTimeRecommendationDto
    {
        public string RecommendedDay { get; set; } = "";
        public string RecommendedHour { get; set; } = "";
        public string Reason { get; set; } = "";
        public int TokensUsed { get; set; }
    }
    public class RenderRequestDto
    {
        public string TemplateId { get; set; } = "";
        public Dictionary<string, string> Slots { get; set; } = [];
    }

    public class RenderResponseDto
    {
        public bool Success { get; set; }
        public string Html { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public int TemplateVersion { get; set; }
        public List<RenderValidationIssueDto> Issues { get; set; } = [];
        public Dictionary<string, string> AppliedSlots { get; set; } = [];
    }

    public class RenderValidationIssueDto
    {
        public string Severity { get; set; } = "";
        public string Slot { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class TemplateSchemaDto
    {
        public string Id { get; set; } = "";
        public int Version { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string PreviewImageUrl { get; set; } = "";
        public string[] SupportedObjectives { get; set; } = [];
        public string[] SupportedTones { get; set; } = [];
        public string[] SupportedChannels { get; set; } = [];
        public Dictionary<string, SlotDefinitionDto> Slots { get; set; } = [];
        public bool IsRecommended { get; set; }
        public string? RecommendationReason { get; set; }
    }

    public class SlotDefinitionDto
    {
        public string Type { get; set; } = "";
        public bool Required { get; set; }
        public int MaxLength { get; set; }
        public string Label { get; set; } = "";
    }

    public class CampaignAnalysisDto
    {
        public List<string> WhatWorked { get; set; } = new();
        public List<string> WhatToImprove { get; set; } = new();
        public string SuggestedNextAction { get; set; } = "";
    }
    public class CampaignExecutionResultDto
    {
        public int CampaignId { get; set; }
        public int TotalReach { get; set; }
        public int Delivered { get; set; }
        public string Status { get; set; } = "";
    }
    public class CampaignSimulationDto
    {
        public int ExpectedDelivered { get; set; }
        public float ExpectedOpenRate { get; set; }
        public float ExpectedClickRate { get; set; }
        public string RiskLevel { get; set; } = "";  // low / medium / high
        public List<string> RiskFactors { get; set; } = new();
        public List<string> OptimisationTips { get; set; } = new();
    }
    public class DecisionFlowDto
    {
        public string EngagementSignal { get; set; } = "";
        public string EngagementConclusion { get; set; } = "";
        public string ValueSignal { get; set; } = "";
        public string ValueConclusion { get; set; } = "";
        public string ChannelSignal { get; set; } = "";

        public string ChannelConclusion { get; set; } = "";

        public string FinalDecision { get; set; } = "";
    }
}