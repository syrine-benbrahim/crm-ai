namespace crm_ai.DTOs
{
    public class ConversationMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    public class ConversationRequestDto
    {
        public List<ConversationMessage> Messages { get; set; } = new();
        public SelectionGroupDto? CurrentRootGroup { get; set; }
        public string? Name { get; set; }
        public bool? Confirmed { get; set; } // ← ADD THIS
    }

    public class ConversationResponseDto
    {
        /// <summary>
        /// "clarifying"           → show questions, keep chat open
        /// "pending_confirmation" → show built selection, ask user to confirm  ← NEW
        /// "completed"            → user confirmed, offer save/preview
        /// "error"                → something went wrong
        /// </summary>
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public List<string> Questions { get; set; } = new();
        public AiSelectionResponseDto? Selection { get; set; }
        public int TokensUsed { get; set; }
    }
}