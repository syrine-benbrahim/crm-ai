namespace crm_ai.Models
{
    public class Brand
    {
        public int Id { get; set; }
        public string Name { get; set; }

        public List<Site> Sites { get; set; }
    }
}
