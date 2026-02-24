namespace crm_ai.Models
{
    public class SelectionGroup
    {
        public int Id { get; set; }

        public int SelectionId { get; set; }
        public Selection Selection { get; set; }

        public int? ParentGroupId { get; set; }
        public SelectionGroup ParentGroup { get; set; }

        public List<SelectionGroup> ChildGroups { get; set; }

        public string LogicalOperator { get; set; }

        public List<SelectionRule> Rules { get; set; }
    }

}
