using crm_ai.DTOs;
using crm_ai.Helpers;
using crm_ai.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace crm_ai.Tests
{
    public class SelectionSanitizerTests
    {
        private static Dictionary<int, NodeCatalogItem> BuildCatalog() => new()
        {
            [4] = new()
            {
                Id = 4,
                ParentName = "Age",
                DataType = "agerange",
                Category = "Age"
            },
            [5] = new()
            {
                Id = 5,
                ParentName = "Age",
                DataType = "agerange",
                Category = "Age"
            },
            [6] = new()
            {
                Id = 6,
                ParentName = "Age",
                DataType = "agerange",
                Category = "Age"
            },
            [13] = new()
            {
                Id = 13,
                ParentName = "Gender",
                DataType = "string",
                Category = "Gender"
            },
            [14] = new()
            {
                Id = 14,
                ParentName = "Gender",
                DataType = "string",
                Category = "Gender"
            },
            [5503] = new()
            {
                Id = 5503,
                ParentName = "Segment",
                DataType = "loyaltysegment",
                Category = "Loyalty"
            },
            [5504] = new()
            {
                Id = 5504,
                ParentName = "Segment",
                DataType = "loyaltysegment",
                Category = "Loyalty"
            },
            [5221] = new()
            {
                Id = 5221,
                ParentName = "Regions",
                DataType = "cityregion",
                Category = "Location"
            },
            [5503] = new()
            {
                Id = 5503,
                ParentName = "Segment",
                DataType = "loyaltysegment",
                Category = "Loyalty"
            },
            [5350] = new()
            {
                Id = 5350,
                ParentName = "Recency",
                DataType = "visitrecency",
                Category = "Recency"
            },
        };

        private static SelectionRuleDto Rule(int id) =>
            new() { TreeNodeId = id, Operator = "=", Value = "" };

        // ── Impossible AND cases — must be converted ─────────────────────

        [Fact]
        public void TwoAgeRanges_InAnd_ConvertedToOrSubGroup()
        {
            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new() { Rule(5), Rule(6) },
                Groups = new()
            };

            var result = SelectionSanitizer.Sanitize(
                group, BuildCatalog(), NullLogger.Instance);

            Assert.Empty(result.Rules);
            Assert.Single(result.Groups);
            Assert.Equal("OR", result.Groups[0].LogicalOperator);
            Assert.Equal(2, result.Groups[0].Rules.Count);
        }

        [Fact]
        public void TwoGenders_InAnd_ConvertedToOrSubGroup()
        {
            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new() { Rule(13), Rule(14) },
                Groups = new()
            };

            var result = SelectionSanitizer.Sanitize(
                group, BuildCatalog(), NullLogger.Instance);

            Assert.Empty(result.Rules);
            Assert.Single(result.Groups);
            Assert.Equal("OR", result.Groups[0].LogicalOperator);
        }

        [Fact]
        public void TwoLoyaltyTiers_InAnd_ConvertedToOrSubGroup()
        {
            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new() { Rule(5503), Rule(5504) },
                Groups = new()
            };

            var result = SelectionSanitizer.Sanitize(
                group, BuildCatalog(), NullLogger.Instance);

            Assert.Empty(result.Rules);
            Assert.Single(result.Groups);
            Assert.Equal("OR", result.Groups[0].LogicalOperator);
        }

        [Fact]
        public void ThreeAgesAndTwoLoyalty_BothFixed_TwoOrSubGroups()
        {
            // AND { 18-24, 25-34, 35-44, Loyal, Frequent }
            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new() { Rule(4), Rule(5), Rule(6), Rule(5503), Rule(5504) },
                Groups = new()
            };

            var result = SelectionSanitizer.Sanitize(
                group, BuildCatalog(), NullLogger.Instance);

            Assert.Empty(result.Rules);
            Assert.Equal(2, result.Groups.Count);
            Assert.All(result.Groups, g => Assert.Equal("OR", g.LogicalOperator));
        }

        // ── Valid AND cases — must NOT be touched ────────────────────────

        [Fact]
        public void DifferentCategories_InAnd_NotTouched()
        {
            // Female + London + Loyal — all different, AND is correct
            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new() { Rule(14), Rule(5221), Rule(5503) },
                Groups = new()
            };

            var result = SelectionSanitizer.Sanitize(
                group, BuildCatalog(), NullLogger.Instance);

            Assert.Equal(3, result.Rules.Count);
            Assert.Empty(result.Groups);
        }

        [Fact]
        public void SingleRule_NotTouched()
        {
            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new() { Rule(14) },
                Groups = new()
            };

            var result = SelectionSanitizer.Sanitize(
                group, BuildCatalog(), NullLogger.Instance);

            Assert.Single(result.Rules);
            Assert.Empty(result.Groups);
        }

        // ── OR groups — must never be touched ───────────────────────────

        [Fact]
        public void OrGroup_WithAgeRanges_NeverTouched()
        {
            var group = new SelectionGroupDto
            {
                LogicalOperator = "OR",
                Rules = new() { Rule(5), Rule(6) },
                Groups = new()
            };

            var result = SelectionSanitizer.Sanitize(
                group, BuildCatalog(), NullLogger.Instance);

            Assert.Equal("OR", result.LogicalOperator);
            Assert.Equal(2, result.Rules.Count);
            Assert.Empty(result.Groups);
        }

        // ── Nested groups — fix recursively ─────────────────────────────

        [Fact]
        public void NestedAndGroup_WithTwoAges_FixedRecursively()
        {
            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new() { Rule(14) },
                Groups = new()
        {
            new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new() { Rule(5), Rule(6) },
                Groups = new()
            }
        }
            };

            var result = SelectionSanitizer.Sanitize(
                group, BuildCatalog(), NullLogger.Instance);

            Assert.Single(result.Rules);                              // Female untouched
            Assert.Single(result.Groups);                             // one child group
            Assert.Empty(result.Groups[0].Rules);                     // child rules moved out
            Assert.Single(result.Groups[0].Groups);                   // child has one sub-group
            Assert.Equal("OR", result.Groups[0].Groups[0].LogicalOperator); // that sub-group is OR
            Assert.Equal(2, result.Groups[0].Groups[0].Rules.Count);  // both ages inside
        }
    }
}