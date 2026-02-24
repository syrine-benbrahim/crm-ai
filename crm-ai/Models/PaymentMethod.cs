namespace crm_ai.Models
{
    public class PaymentMethod
    {
        public int Id { get; set; }
        public string Name { get; set; }

        public List<Transaction> Transactions { get; set; }
    }
}
