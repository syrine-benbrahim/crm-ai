using crm_ai.DTOs;
using crm_ai.Helpers;
using crm_ai.Services;
using Xunit;

namespace crm_ai.Tests
{
    public class BuildGroupFromAnswersTests
    {
        private static ResolvedClarificationAnswer Answer(
            string blockId, params int[] nodeIds) =>
            new()
            {
                BlockId = blockId,
                ResolvedRules = nodeIds.Select(id =>
                    new ClarificationRuleDto
                    {
                        TreeNodeId = id,
                        Operator = "=",
                        Value = ""
                    }).ToList()
            };

        [Fact]
        public void SingleBlock_SingleRule_ReturnsAndWithOneRule()
        {
            var result = SelectionGroupBuilder.BuildGroupFromResolvedAnswers(
                new() { Answer("block_1", 5503) });

            Assert.Equal("AND", result.LogicalOperator);
            Assert.Single(result.Rules);
            Assert.Empty(result.Groups);
        }

        [Fact]
        public void SingleBlock_MultipleRules_ReturnsOrGroup()
        {
            var result = SelectionGroupBuilder.BuildGroupFromResolvedAnswers(
                new() { Answer("block_1", 5, 6) });

            Assert.Equal("OR", result.LogicalOperator);
            Assert.Equal(2, result.Rules.Count);
            Assert.Empty(result.Groups);
        }

        [Fact]
        public void TwoBlocks_OneRuleEach_ReturnsFlatOrGroup()
        {
            // User picked one Total spend + one Average spend
            // Both resolve the same "spend a lot" ambiguity → flat OR
            var result = SelectionGroupBuilder.BuildGroupFromResolvedAnswers(
                new() { Answer("block_total", 5380), Answer("block_avg", 5368) });

            Assert.Equal("OR", result.LogicalOperator);
            Assert.Equal(2, result.Rules.Count);
            Assert.Empty(result.Groups);
        }

        [Fact]
        public void TwoBlocks_MixedRules_ReturnsAndWithOrSubGroups()
        {
            // Block 1: two ages, Block 2: one loyalty → AND with OR sub-groups
            var result = SelectionGroupBuilder.BuildGroupFromResolvedAnswers(
                new()
                {
                    Answer("block_age", 5, 6),
                    Answer("block_loyalty", 5503)
                });

            Assert.Equal("AND", result.LogicalOperator);
            Assert.Equal(2, result.Groups.Count);
        }

        [Fact]
        public void EmptyAnswers_ReturnsEmptyAndGroup()
        {
            var result = SelectionGroupBuilder.BuildGroupFromResolvedAnswers(new());

            Assert.Equal("AND", result.LogicalOperator);
            Assert.Empty(result.Rules);
            Assert.Empty(result.Groups);
        }

        [Fact]
        public void AllFallbacks_TreatedAsEmpty()
        {
            // Answers with no resolved rules (all fallbacks selected)
            var result = SelectionGroupBuilder.BuildGroupFromResolvedAnswers(
                new()
                {
                    new ResolvedClarificationAnswer
                    {
                        BlockId = "block_1",
                        ResolvedRules = new() // empty = fallback
                    }
                });

            Assert.Equal("AND", result.LogicalOperator);
            Assert.Empty(result.Rules);
        }
    }
}