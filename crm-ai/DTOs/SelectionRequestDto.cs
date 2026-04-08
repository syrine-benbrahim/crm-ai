namespace crm_ai.DTOs
{
    public class SelectionRequestDto
    {
        public string Name { get; set; }
        public string? Description { get; set; }  // optional — null, manual, or AI-generated
        public SelectionGroupDto RootGroup { get; set; }
    }
}