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
    }

    public class LinkSelectionDto
    {
        public int SelectionId { get; set; }
    }
}