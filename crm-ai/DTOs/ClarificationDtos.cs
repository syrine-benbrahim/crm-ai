using crm_ai.DTOs;

namespace crm_ai.DTOs
{
    public sealed class ClarificationRuleDto
    {
        public int TreeNodeId { get; set; }
        public string Operator { get; set; } = "=";
        public string Value { get; set; } = "";
    }

    public sealed class ClarificationOptionDto
    {
        public string OptionId { get; set; } = "";
        public string Label { get; set; } = "";
        public List<ClarificationRuleDto> Rules { get; set; } = new();
        public bool IsFallback { get; set; } = false;
    }

    public sealed class ClarificationBlockDto
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "single_choice";
        public string Label { get; set; } = "";
        public List<ClarificationOptionDto> Options { get; set; } = new();
    }

    public sealed class ClarificationState
    {
        public string Id { get; set; } = "";
        public List<ClarificationBlockDto> Blocks { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class ClarificationAnswerDto
    {
        public string BlockId { get; set; } = "";
        public List<string> SelectedOptionIds { get; set; } = new();
    }
}