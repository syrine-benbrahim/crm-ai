namespace crm_ai.DTOs
{
    public class ConfidenceRequestDto
    {
        public string Intent { get; set; } = string.Empty;
        public SelectionGroupDto RootGroup { get; set; } = new();
    }
}