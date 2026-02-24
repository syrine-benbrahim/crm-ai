namespace crm_ai.Models
{
    public class SelectionRule
    {
        public int Id { get; set; }

        public int GroupId { get; set; }
        public SelectionGroup Group { get; set; }

        public int TreeNodeId { get; set; }
        public TreeNode TreeNode { get; set; }

        public string Operator { get; set; }
        public string Value { get; set; }
    }

}
