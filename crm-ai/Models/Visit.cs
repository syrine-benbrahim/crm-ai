namespace crm_ai.Models
{
    public class Visit
    {
        public int Id { get; set; }

        public int CustomerId { get; set; }
        public Customer Customer { get; set; }

        public DateTime VisitDateTime { get; set; }

        public decimal TransactionValue { get; set; }
        public bool IsLoyalty { get; set; }

        public string Source { get; set; }

        public int SiteId { get; set; }
        public Site Site { get; set; }
    }

}
