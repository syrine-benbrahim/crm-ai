namespace crm_ai.Models
{
    public class Selection
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string Status { get; set; } = "Active";
        public List<SelectionGroup> Groups { get; set; }
        public List<SelectionExecution> SelectionExecutions { get; set; }
    }
}