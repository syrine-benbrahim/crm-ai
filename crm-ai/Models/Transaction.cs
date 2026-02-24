namespace crm_ai.Models
{
    public class Transaction
    {
        public int Id { get; set; }

        public int CustomerId { get; set; }
        public Customer Customer { get; set; }

        public DateTime TransactionDate { get; set; }
        public decimal TotalSpend { get; set; }

        public int PaymentMethodId { get; set; }
        public PaymentMethod PaymentMethod { get; set; }

        public int OrderStatusId { get; set; }
        public OrderStatus OrderStatus { get; set; }

        public int SiteId { get; set; }
        public Site Site { get; set; }
    }

}
