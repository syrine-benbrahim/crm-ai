namespace crm_ai.Models
{
    public class CustomerAddress
    {
        public int Id { get; set; }

        public int CustomerId { get; set; }
        public Customer Customer { get; set; }

        public string City { get; set; }
        public string Country { get; set; }
        public string PostalCode { get; set; }
    }
}
