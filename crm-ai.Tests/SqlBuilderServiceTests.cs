using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Models;
using crm_ai.Services;
using Microsoft.EntityFrameworkCore;

namespace crm_ai.Tests
{
    public class SqlBuilderServiceTests
    {
        private AppDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            return new AppDbContext(options);
        }

        // nodeName = the filter value the service reads from NodeName
        // fieldName = the database column name
        private TreeNode MakeNode(
            int id,
            string fieldName,
            string dataType,
            string entityName = "Customer",
            string? nodeName = null)
        {
            return new TreeNode
            {
                Id = id,
                NodeCode = $"NODE_{id}",
                NodeName = nodeName ?? fieldName,
                FieldName = fieldName,
                DataType = dataType,
                EntityName = entityName,
                IsSelectable = 1,
                ParentId = null
            };
        }

        // ── LOGICAL OPERATOR TESTS ───────────────────────────────────────

        [Fact]
        public async Task InvalidLogicalOperator_ThrowsArgumentException()
        {
            var context = CreateContext();
            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "INVALID",
                Rules = new List<SelectionRuleDto>()
            };

            await Assert.ThrowsAsync<ArgumentException>(
                () => service.BuildQueryPartsAsync(group));
        }

        [Fact]
        public async Task AndOperator_MultipleRules_JoinsWithAnd()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Gender", "string", nodeName: "Male"));
            context.TreeNodes.Add(MakeNode(2, "Email", "string", nodeName: "test@test.com"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" },
                    new() { TreeNodeId = 2, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("AND", where);
        }

        [Fact]
        public async Task OrOperator_MultipleRules_JoinsWithOr()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Gender", "string", nodeName: "Male"));
            context.TreeNodes.Add(MakeNode(2, "Gender", "string", nodeName: "Female"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "OR",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" },
                    new() { TreeNodeId = 2, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("OR", where);
        }

        [Fact]
        public async Task ExcludeOperator_ReturnsNotCondition()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "City", "string", nodeName: "London"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "EXCLUDE",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("NOT", where);
        }

        // ── STRING TESTS ─────────────────────────────────────────────────

        [Fact]
        public async Task StringField_IsOperator_ReturnsCorrectClause()
        {
            var context = CreateContext();
            // NodeName = the value the service will use in the SQL
            context.TreeNodes.Add(
                MakeNode(1, "Gender", "string", nodeName: "Male"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("c.Gender", where);
            Assert.Contains("Male", where);
        }

        [Fact]
        public async Task StringField_ContainsOperator_ReturnsLikeClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "Gender", "string", nodeName: "Male"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "CONTAINS", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("LIKE", where);
        }

        [Fact]
        public async Task StringField_SqlInjectionAttempt_IsEscaped()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "Gender", "string",
                    nodeName: "'; DROP TABLE Customers--"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("''", where); // single quotes escaped
        }

        // ── NUMBER TESTS ─────────────────────────────────────────────────

        [Fact]
        public async Task NumberField_Range_ReturnsBetweenClause()
        {
            var context = CreateContext();
            // NodeName = "18-24" — what the service parses
            context.TreeNodes.Add(
                MakeNode(1, "Age", "number", nodeName: "18-24"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("BETWEEN", where);
            Assert.Contains("18", where);
            Assert.Contains("24", where);
        }

        [Fact]
        public async Task NumberField_PlusSign_ReturnsGreaterThanOrEqual()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "Age", "number", nodeName: "25+"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains(">=", where);
            Assert.Contains("25", where);
        }

        [Fact]
        public async Task NumberField_InvalidValue_ThrowsArgumentException()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "Age", "number", nodeName: "not_a_number"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            await Assert.ThrowsAsync<ArgumentException>(
                () => service.BuildQueryPartsAsync(group));
        }

        [Fact]
        public async Task NumberField_InvalidRange_ThrowsArgumentException()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "Age", "number", nodeName: "1-2-3"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            await Assert.ThrowsAsync<ArgumentException>(
                () => service.BuildQueryPartsAsync(group));
        }

        // ── BOOL TESTS ───────────────────────────────────────────────────

        [Fact]
        public async Task BoolField_True_Returns1()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "IsLoyalty", "bool", nodeName: "IsLoyalty"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "true" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("= 1", where);
        }

        [Fact]
        public async Task BoolField_False_Returns0()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "IsLoyalty", "bool", nodeName: "IsLoyalty"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "false" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("= 0", where);
        }

        // ── JOIN TESTS ───────────────────────────────────────────────────

        [Fact]
        public async Task CustomerAddressField_ReturnsJoinClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "City", "string", "CustomerAddress",
                    nodeName: "London"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (_, join) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("LEFT JOIN CustomerAddresses", join);
        }

        [Fact]
        public async Task CustomerField_NoJoinClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "Gender", "string", "Customer",
                    nodeName: "Male"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (_, join) = await service.BuildQueryPartsAsync(group);
            Assert.Empty(join);
        }

        // ── VISIT COUNT TESTS ────────────────────────────────────────────

        [Fact]
        public async Task VisitCount_PlusSign_ReturnsGreaterThanOrEqual()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "CustomerId", "visitcount", "Visit",
                    nodeName: "5+"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains(">=", where);
            Assert.Contains("COUNT(*)", where);
        }

        [Fact]
        public async Task VisitCount_ExactValue_ReturnsEqualClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "CustomerId", "visitcount", "Visit",
                    nodeName: "3"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("= 3", where);
            Assert.Contains("COUNT(*)", where);
        }

        // ── SPEND RANGE TESTS ────────────────────────────────────────────

        [Fact]
        public async Task SpendRange_LessThan_ReturnsLessThanClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "TotalSpend", "spendrange", nodeName: "<£10"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("<", where);
            Assert.Contains("10", where);
        }

        [Fact]
        public async Task SpendRange_Range_ReturnsBetweenClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "TotalSpend", "spendrange", nodeName: "£10-£20"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("BETWEEN", where);
            Assert.Contains("10", where);
            Assert.Contains("20", where);
        }

        [Fact]
        public async Task SpendRange_Plus_ReturnsGreaterThanOrEqual()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "TotalSpend", "spendrange", nodeName: "£600+"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains(">=", where);
            Assert.Contains("600", where);
        }

        // ── VISIT RECENCY TESTS ──────────────────────────────────────────

        [Fact]
        public async Task VisitRecency_Yesterday_ReturnsCorrectClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "VisitDateTime", "visitrecency", "Visit",
                    nodeName: "Yesterday"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("MAX(VisitDateTime)", where);
            Assert.Contains("DATEADD(DAY,-1", where);
        }

        [Fact]
        public async Task VisitRecency_MonthRange_ReturnsBetweenClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "VisitDateTime", "visitrecency", "Visit",
                    nodeName: "1-2 months"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("BETWEEN", where);
            Assert.Contains("DATEADD(MONTH,-2", where);
            Assert.Contains("DATEADD(MONTH,-1", where);
        }

        [Fact]
        public async Task VisitRecency_UnknownValue_ReturnsEmpty()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "VisitDateTime", "visitrecency", "Visit",
                    nodeName: "unknown value"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Equal("1=1", where);
        }

        // ── CITY REGION TESTS ────────────────────────────────────────────

        [Fact]
        public async Task CityRegion_KnownRegion_ReturnsInClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "City", "cityregion", "CustomerAddress",
                    nodeName: "East"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("IN", where);
            Assert.Contains("Cambridge", where);
        }

        [Fact]
        public async Task CityRegion_UnknownRegion_ReturnsEmpty()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "City", "cityregion", "CustomerAddress",
                    nodeName: "UnknownRegion"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Equal("1=1", where);
        }

        [Fact]
        public async Task CityRegion_ReturnsJoinClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(
                MakeNode(1, "City", "cityregion", "CustomerAddress",
                    nodeName: "Wales"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new() { TreeNodeId = 1, Operator = "=", Value = "" }
                }
            };

            var (_, join) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("LEFT JOIN CustomerAddresses", join);
        }

        // ── NESTED GROUP TESTS ───────────────────────────────────────────

        [Fact]
        public async Task NestedGroups_OrOperator_ReturnsCorrectClause()
        {
            var context = CreateContext();
            // Use string fields for both nodes — nodeName is the value
            context.TreeNodes.Add(
                MakeNode(1, "Gender", "string", nodeName: "Female"));
            context.TreeNodes.Add(
                MakeNode(2, "Gender", "string", nodeName: "Male"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "OR",
                Rules = new List<SelectionRuleDto>(),
                Groups = new List<SelectionGroupDto>
                {
                    new SelectionGroupDto
                    {
                        LogicalOperator = "AND",
                        Rules = new List<SelectionRuleDto>
                        {
                            new() { TreeNodeId = 1, Operator = "=", Value = "" }
                        }
                    },
                    new SelectionGroupDto
                    {
                        LogicalOperator = "AND",
                        Rules = new List<SelectionRuleDto>
                        {
                            new() { TreeNodeId = 2, Operator = "=", Value = "" }
                        }
                    }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Contains("OR", where);
            Assert.Contains("Female", where);
            Assert.Contains("Male", where);
        }

        // ── EMPTY RULES TEST ─────────────────────────────────────────────

        [Fact]
        public async Task EmptyRules_Returns1Equals1()
        {
            var context = CreateContext();
            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>()
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);
            Assert.Equal("1=1", where);
        }
    }
}