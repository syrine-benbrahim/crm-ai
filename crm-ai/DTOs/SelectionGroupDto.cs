namespace crm_ai.DTOs
{
    public class SelectionGroupDto
    {
        public string LogicalOperator { get; set; }

        public List<SelectionRuleDto> Rules { get; set; }

        public List<SelectionGroupDto> Groups { get; set; }
    }

}
