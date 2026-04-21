namespace crm_ai.DTOs
{
    public class TreeNodeDto
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public bool IsSelectable { get; set; }
        public string? DataType { get; set; }
        public string? EntityName { get; set; }
        public string? FieldName { get; set; }
        public List<TreeNodeDto> Children { get; set; } = new();

        // NEW — exposed so the frontend admin can display/edit these
        public string? AiLabel { get; set; }
        public string? SemanticCategory { get; set; }
    }
}