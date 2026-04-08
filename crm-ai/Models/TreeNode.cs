namespace crm_ai.Models
{
    public class TreeNode
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public TreeNode? Parent { get; set; }  
        public List<TreeNode>? Children { get; set; }  
        public string NodeCode { get; set; }
        public string NodeName { get; set; }
        public string? NodeDesc { get; set; }  
        public string? EntityName { get; set; }  
        public string? FieldName { get; set; }  
        public string? DataType { get; set; }  
        public int IsSelectable { get; set; }  
    }
}