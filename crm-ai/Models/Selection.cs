namespace crm_ai.Models
{
    public class Selection
    {
        public int Id { get; set; }

        public string Name { get; set; }
        public DateTime CreatedAt { get; set; }

        public List<SelectionGroup> Groups { get; set; }
    }

}
