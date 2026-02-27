namespace crm_ai.Models
{
    public class SelectionExecution
    {
        public int Id { get; set; }
        public int SelectionId { get; set; }
        public Selection Selection { get; set; }
        public DateTime ExecutedAt { get; set; }
        public int TotalUsers { get; set; }
        public string EmailsJson { get; set; }  
    }
}