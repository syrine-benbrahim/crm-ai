using crm_ai.DTOs;

namespace crm_ai.Helpers
{
    public static class SelectionGroupBuilder
    {
        public static SelectionGroupDto BuildGroupFromResolvedAnswers(
            List<ResolvedClarificationAnswer> answers)
        {
            var meaningful = answers.Where(a => a.ResolvedRules.Count > 0).ToList();

            if (meaningful.Count == 0)
                return new SelectionGroupDto
                {
                    LogicalOperator = "AND",
                    Rules = new(),
                    Groups = new()
                };

            if (meaningful.Count == 1 && meaningful[0].ResolvedRules.Count == 1)
            {
                var r = meaningful[0].ResolvedRules[0];
                return new SelectionGroupDto
                {
                    LogicalOperator = "AND",
                    Rules = new() { new() { TreeNodeId = r.TreeNodeId, Operator = r.Operator, Value = r.Value } },
                    Groups = new()
                };
            }

            if (meaningful.Count == 1)
                return new SelectionGroupDto
                {
                    LogicalOperator = "OR",
                    Rules = meaningful[0].ResolvedRules.Select(r =>
                        new SelectionRuleDto { TreeNodeId = r.TreeNodeId, Operator = r.Operator, Value = r.Value }
                    ).ToList(),
                    Groups = new()
                };

            var allRules = meaningful
                .SelectMany(a => a.ResolvedRules)
                .Select(r => new SelectionRuleDto
                {
                    TreeNodeId = r.TreeNodeId,
                    Operator = r.Operator,
                    Value = r.Value
                }).ToList();

            if (meaningful.All(a => a.ResolvedRules.Count == 1))
                return new SelectionGroupDto
                {
                    LogicalOperator = "OR",
                    Rules = allRules,
                    Groups = new()
                };

            return new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new(),
                Groups = meaningful.Select(a => new SelectionGroupDto
                {
                    LogicalOperator = a.ResolvedRules.Count == 1 ? "AND" : "OR",
                    Rules = a.ResolvedRules.Select(r =>
                        new SelectionRuleDto { TreeNodeId = r.TreeNodeId, Operator = r.Operator, Value = r.Value }
                    ).ToList(),
                    Groups = new()
                }).ToList()
            };
        }
    }

    public class ResolvedClarificationAnswer
    {
        public string BlockId { get; set; } = "";
        public List<ClarificationRuleDto> ResolvedRules { get; set; } = new();
    }
}