namespace crm_ai.Models
{
    public class CampaignContent
    {
        public int Id { get; set; }
        public int CampaignId { get; set; }
        public Campaign Campaign { get; set; } = null!;
        public string ContentType { get; set; } = ""; // "Email" | "SMS"
        public string? Subject { get; set; }
        public string? HtmlBody { get; set; }
        public string? SmsText { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    }
}