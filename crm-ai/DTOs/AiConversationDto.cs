namespace crm_ai.DTOs
{
    // ════════════════════════════════════════════════════════════════════════
    // ConversationRequestDto
    //
    // Sent by the frontend on every conversation turn.
    //
    // Flow flags:
    //   IntentConfirmed = false (default)
    //     → AI returns "intent_confirmation" (summarises what it understood)
    //     → Frontend shows summary + "Yes, build it" button
    //
    //   IntentConfirmed = true
    //     → AI proceeds to build the rule tree
    //     → Returns "pending_confirmation" with description
    //     → Frontend shows "Looks good / Change it" buttons
    //
    //   Confirmed = true
    //     → User approved the pending_confirmation card
    //     → AI skips rebuild and returns "completed" immediately
    // ═══════════════════════════════════════════════════════════════════════

    public class ConversationMessage
    {
        public string Role { get; set; } = "user";    // "user" or "assistant"
        public string Content { get; set; } = "";
    }

    public class ConversationResponseDto
    {
        public string Status { get; set; } = "";
        public string Message { get; set; } = "";
        public List<string> Questions { get; set; } = new();
        public List<ClarificationBlockDto> Clarifications { get; set; } = new();
        public string? ClarificationStateId { get; set; }           // ADD THIS
        public AiSelectionResponseDto? Selection { get; set; }
        public int TokensUsed { get; set; }
    }

    public class ConversationRequestDto
    {
        public List<ConversationMessage> Messages { get; set; } = new();
        public SelectionGroupDto? CurrentRootGroup { get; set; }
        public bool? IntentConfirmed { get; set; }
        public bool? Confirmed { get; set; }
        public string? Name { get; set; }
        public string? ClarificationStateId { get; set; }           // ADD THIS
        public List<ClarificationAnswerDto>? ClarificationAnswers { get; set; }
    }
}