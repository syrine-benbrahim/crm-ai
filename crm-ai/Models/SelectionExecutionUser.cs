namespace crm_ai.Models
{
    public class SelectionExecutionUser
    {
        public int Id { get; set; }

        public int SelectionExecutionId { get; set; }
        public SelectionExecution SelectionExecution { get; set; }

        public int CustomerId { get; set; }   
    }
}