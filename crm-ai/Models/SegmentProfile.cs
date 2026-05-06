namespace crm_ai.Models
{
    /// <summary>
    /// Computed by C# from the selection's rule tree + live customer data.
    /// This — not raw JSON — is what the AI receives when generating strategy.
    /// Keeps AI input clean, deterministic, and explainable.
    /// </summary>
    public class SegmentProfile
    {
        public int AudienceSize { get; set; }
        public float EmailCoveragePercent { get; set; }   // % who have a valid email
        public float SmsCoveragePercent { get; set; }     // % who have a valid mobile

        // Dominant characteristics derived from the rule tree
        public string DominantRecency { get; set; } = "";          // e.g. "2-3 months lapsed"
        public string DominantLoyaltyTier { get; set; } = "";      // e.g. "Loyal", "Lapsed"
        public string DominantSpendTier { get; set; } = "";        // e.g. "High (£50+)"
        public string[] DominantLocations { get; set; } = [];      // e.g. ["London", "Manchester"]
        public string[] AgeRanges { get; set; } = [];              // e.g. ["25-34", "35-44"]
        public string? Gender { get; set; }                        // "Female" | "Male" | null (mixed)

        // Behavioural classification — C# derives this from recency + loyalty
        public EngagementLevel EngagementLevel { get; set; }       // Active | AtRisk | Lapsed | LongTermLapsed
        public CustomerValueTier ValueTier { get; set; }           // High | Medium | Low

        // One human-readable sentence C# builds — the AI uses this as primary input
        public string BehaviourSummary { get; set; } = "";

        // Best send window computed from visit history (null if no data)
        public string? RecommendedSendDay { get; set; }    // e.g. "Thursday"
        public string? RecommendedSendHour { get; set; }   // e.g. "12:00"
    }

    public enum EngagementLevel
    {
        Active,          // visited within 1 month
        AtRisk,          // 1-3 months
        Lapsed,          // 3-6 months
        LongTermLapsed   // 6+ months
    }

    public enum CustomerValueTier
    {
        High,    // spend £50+
        Medium,  // spend £20-£49
        Low      // spend <£20 or unknown
    }

    /// <summary>
    /// Metadata for each template — stored in templates.json, never in DB.
    /// The AI recommendation engine matches strategy output against these tags.
    /// </summary>
    public class TemplateMetadata
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string PreviewImageUrl { get; set; } = "";
        public string FilePath { get; set; } = "";           // path to .html on disk

        // These tags are what the AI matches against
        public string[] SupportedObjectives { get; set; } = [];  // "reactivation", "retention", "conversion"
        public string[] SupportedTones { get; set; } = [];       // "urgent", "friendly", "premium", "promotional"
        public string[] SupportedChannels { get; set; } = [];    // "Email", "SMS"

        // Slot names this template exposes for AI content injection
        public string[] ContentSlots { get; set; } = [];
        // e.g. ["subject", "preheader", "hero_headline", "body_para_1", "cta_text", "cta_url"]
    }
}