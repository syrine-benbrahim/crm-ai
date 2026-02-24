namespace crm_ai.Models
{
    public class BookingStatus
    {
        public int Id { get; set; }
        public string Name { get; set; }

        public List<Booking> Bookings { get; set; }
    }
}
