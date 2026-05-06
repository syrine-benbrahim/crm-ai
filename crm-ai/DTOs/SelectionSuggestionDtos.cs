namespace crm_ai.DTOs
{
    // ── Request ───────────────────────────────────────────────────────────
    public class SuggestSelectionsRequestDto
    {
        public string Objective { get; set; } = "";
        public string Channel { get; set; } = "";
        public string? CampaignName { get; set; }
        public int MaxResults { get; set; } = 5;
    }

    // ── Response ─────────────────────────────────────────────────────────
    public class SelectionSuggestionResultDto
    {
        /// <summary>False when no active selections exist in the system.</summary>
        public bool HasSelections { get; set; }

        /// <summary>
        /// True when at least one selection scored above GoodMatchThreshold.
        /// When true: RecommendedId is set, SuggestedPrompt is null.
        /// When false: RecommendedId is null, SuggestedPrompt is populated.
        /// </summary>
        public bool HasGoodMatch { get; set; }

        /// <summary>The single recommended selection ID. Null when HasGoodMatch=false.</summary>
        public int? RecommendedId { get; set; }

        /// <summary>
        /// Ranked suggestions. When HasGoodMatch=true, recommended is first.
        /// When HasGoodMatch=false, these are shown as fallback "or choose existing".
        /// </summary>
        public List<SelectionSuggestionDto> Suggestions { get; set; } = [];

        /// <summary>
        /// Ready-to-use natural language string for the selection builder.
        /// Populated only when HasGoodMatch=false.
        /// Frontend sends this directly into ContinueConversationAsync
        /// as the first user message — no reformatting needed.
        /// e.g. "Lapsed or long-term lapsed customers who are loyal, emailable"
        /// </summary>
        public string? SuggestedPrompt { get; set; }

        /// <summary>
        /// One-sentence explanation of what SuggestedPrompt will build.
        /// Shown to the user in the UI before they click "Build this".
        /// e.g. "This will find your loyal customers who have stopped visiting."
        /// </summary>
        public string? SuggestedPromptExplanation { get; set; }

        /// <summary>Tokens used by any AI call in this suggestion request.</summary>
        public int TokensUsed { get; set; }
    }

    public class SelectionSuggestionDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }

        /// <summary>
        /// Composite score: Tier1 (0-100 keyword) + Tier2 (0-60 structure).
        /// Not shown to end users. Used internally for ranking.
        /// </summary>
        public int Score { get; set; }

        public bool IsRecommended { get; set; }

        /// <summary>
        /// Human-readable reason built by C#. Never AI-generated.
        /// Only populated when IsRecommended=true.
        /// e.g. "Targets lapsed/inactive customers; includes loyalty segment filter"
        /// </summary>
        public string? RecommendationReason { get; set; }
    }
}