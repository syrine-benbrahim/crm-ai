namespace crm_ai.Models
{
    public class Booking
    {
        public int Id { get; set; }

        public int CustomerId { get; set; }
        public Customer Customer { get; set; }

        public DateTime BookingDateTime { get; set; }
        public DateTime SeatingDateTime { get; set; }

        public int Covers { get; set; }

        public int SiteId { get; set; }
        public Site Site { get; set; }

        public int BookingStatusId { get; set; }
        public BookingStatus BookingStatus { get; set; }
    }

}
