namespace crm_ai.DTOs
{
    public class TreeNodeDto
    {
        public int Id { get; set; }

        public string Label { get; set; }

        public bool IsSelectable { get; set; }

        public string DataType { get; set; }

        public string EntityName { get; set; }

        public string FieldName { get; set; }

        public List<TreeNodeDto> Children { get; set; }
    }

}
