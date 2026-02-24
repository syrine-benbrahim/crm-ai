namespace crm_ai.Models
{
    public class Site
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public int BrandId { get; set; }
        public Brand Brand { get; set; }

        public string City { get; set; }
        public string Country { get; set; }

        public List<Transaction> Transactions { get; set; }
        public List<Visit> Visits { get; set; }
        public List<Booking> Bookings { get; set; }
    }
}
