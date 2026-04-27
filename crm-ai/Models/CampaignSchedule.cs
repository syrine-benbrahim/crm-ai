namespace crm_ai.Models
{
    public class CampaignSchedule
    {
        public int Id { get; set; }
        public int CampaignId { get; set; }
        public Campaign Campaign { get; set; } = null!;
        public DateTime ScheduledAt { get; set; }
        public string TimeZone { get; set; } = "UTC";
        public DateTime? ConfirmedAt { get; set; }
    }
}