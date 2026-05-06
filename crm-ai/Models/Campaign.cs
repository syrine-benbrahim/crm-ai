namespace crm_ai.Models
{
    public class Campaign
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Objective { get; set; }
        public string Channel { get; set; } = ""; // "Email" | "SMS"
        public string Status { get; set; } = "Draft"; // "Draft" | "Active" | "Sent"
        public int? SelectionId { get; set; }
        public Selection? Selection { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public CampaignContent? Content { get; set; }
        public CampaignSchedule? Schedule { get; set; }
        public int TotalReach { get; set; }
        public int Delivered { get; set; }
        public int Opened { get; set; }
        public int Clicked { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}