namespace crm_ai.Models
{
    public class TreeNode
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public TreeNode? Parent { get; set; }  // Add ? here
        public List<TreeNode>? Children { get; set; }  // Add ? here
        public string NodeCode { get; set; }
        public string NodeName { get; set; }
        public string? NodeDesc { get; set; }  // Add ? here
        public string? EntityName { get; set; }  // Add ? here
        public string? FieldName { get; set; }  // Add ? here
        public string? DataType { get; set; }  // Add ? here
        public int IsSelectable { get; set; }  // Change bool to int
    }
}