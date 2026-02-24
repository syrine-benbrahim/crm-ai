namespace crm_ai.Models
{
    public class Customer
    {
        public int Id { get; set; }

        public string FirstName { get; set; }
        public string LastName { get; set; }

        public string Email { get; set; }
        public string Phone { get; set; }

        public DateTime BirthDate { get; set; }
        public string Gender { get; set; }

        public DateTime CreatedAt { get; set; }

        public List<CustomerAddress> Addresses { get; set; }
        public List<Transaction> Transactions { get; set; }
        public List<Visit> Visits { get; set; }
        public List<Booking> Bookings { get; set; }
    }


}
