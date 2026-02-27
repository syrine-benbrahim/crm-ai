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

        private TreeNode MakeNode(int id, string fieldName, string dataType, string entityName = "Customer")
        {
            return new TreeNode
            {
                Id = id,
                NodeCode = $"NODE_{id}",
                NodeName = fieldName,
                FieldName = fieldName,
                DataType = dataType,
                EntityName = entityName,
                IsSelectable = 1,
                ParentId = null
            };
        }

        //  LOGICAL OPERATOR TESTS

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
            context.TreeNodes.Add(MakeNode(1, "Gender", "string"));
            context.TreeNodes.Add(MakeNode(2, "Email", "string"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "Male" },
                    new SelectionRuleDto { TreeNodeId = 2, Operator = "=", Value = "test@test.com" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("AND", where);
        }

        [Fact]
        public async Task OrOperator_MultipleRules_JoinsWithOr()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Gender", "string"));
            context.TreeNodes.Add(MakeNode(2, "Email", "string"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "OR",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "Male" },
                    new SelectionRuleDto { TreeNodeId = 2, Operator = "=", Value = "test@test.com" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("OR", where);
        }

        [Fact]
        public async Task ExcludeOperator_ReturnsNotCondition()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "City", "string"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "EXCLUDE",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "London" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("NOT", where);
        }

        // ✅ STRING TESTS

        [Fact]
        public async Task StringField_IsOperator_ReturnsCorrectClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Email", "string"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "test@test.com" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("c.Email", where);
            Assert.Contains("test@test.com", where);
        }

        [Fact]
        public async Task StringField_ContainsOperator_ReturnsLikeClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Email", "string"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "CONTAINS", Value = "gmail" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("LIKE", where);
            Assert.Contains("%gmail%", where);
        }

        [Fact]
        public async Task StringField_SqlInjectionAttempt_IsEscaped()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Email", "string"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "'; DROP TABLE Customers--" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("''", where);
            Assert.StartsWith("c.Email =", where.Trim());
        }

        // ✅ NUMBER TESTS

        [Fact]
        public async Task NumberField_Range_ReturnsBetweenClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Age", "number"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "18-24" }
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
            context.TreeNodes.Add(MakeNode(1, "Age", "number"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "25+" }
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
            context.TreeNodes.Add(MakeNode(1, "Age", "number"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "not_a_number" }
                }
            };

            await Assert.ThrowsAsync<ArgumentException>(
                () => service.BuildQueryPartsAsync(group));
        }

        [Fact]
        public async Task NumberField_InvalidRange_ThrowsArgumentException()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Age", "number"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "1-2-3" }
                }
            };

            await Assert.ThrowsAsync<ArgumentException>(
                () => service.BuildQueryPartsAsync(group));
        }

        // ✅ BOOL TESTS

        [Fact]
        public async Task BoolField_True_Returns1()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "IsLoyalty", "bool"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "true" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("= 1", where);
        }

        [Fact]
        public async Task BoolField_False_Returns0()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "IsLoyalty", "bool"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "false" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("= 0", where);
        }

        // ✅ JOIN TESTS

        [Fact]
        public async Task CustomerAddressField_ReturnsJoinClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "City", "string", "CustomerAddress"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "London" }
                }
            };

            var (_, join) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("LEFT JOIN CustomerAddresses", join);
        }

        [Fact]
        public async Task CustomerField_NoJoinClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Email", "string", "Customer"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "test@test.com" }
                }
            };

            var (_, join) = await service.BuildQueryPartsAsync(group);

            Assert.Empty(join);
        }

        // ✅ VISIT COUNT TESTS

        [Fact]
        public async Task VisitCount_PlusSign_ReturnsGreaterThanOrEqual()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "VisitCount", "visitcount"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "5+" }
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
            context.TreeNodes.Add(MakeNode(1, "VisitCount", "visitcount"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "3" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("= 3", where);
            Assert.Contains("COUNT(*)", where);
        }

        // ✅ SPEND RANGE TESTS

        [Fact]
        public async Task SpendRange_LessThan_ReturnsLessThanClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "TotalSpend", "spendrange"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "<£10" }
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
            context.TreeNodes.Add(MakeNode(1, "TotalSpend", "spendrange"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "£10-£20" }
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
            context.TreeNodes.Add(MakeNode(1, "TotalSpend", "spendrange"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "£600+" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains(">=", where);
            Assert.Contains("600", where);
        }

        // ✅ VISIT RECENCY TESTS

        [Fact]
        public async Task VisitRecency_Yesterday_ReturnsCorrectClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "VisitDateTime", "visitrecency"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "Yesterday" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("MAX(v.VisitDateTime)", where);
            Assert.Contains("DATEADD(DAY,-1", where);
        }

        [Fact]
        public async Task VisitRecency_MonthRange_ReturnsBetweenClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "VisitDateTime", "visitrecency"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "1-2 months" }
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
            context.TreeNodes.Add(MakeNode(1, "VisitDateTime", "visitrecency"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "unknown value" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Equal("1=1", where);
        }

        // ✅ CITY REGION TESTS

        [Fact]
        public async Task CityRegion_KnownRegion_ReturnsInClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "City", "cityregion", "CustomerAddress"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "East" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("IN", where);
            Assert.Contains("Cambridge", where);
            Assert.Contains("Ipswich", where);
            Assert.Contains("Norwich", where);
            Assert.Contains("Peterborough", where);
        }

        [Fact]
        public async Task CityRegion_UnknownRegion_ReturnsEmpty()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "City", "cityregion", "CustomerAddress"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "UnknownRegion" }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Equal("1=1", where);
        }

        [Fact]
        public async Task CityRegion_ReturnsJoinClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "City", "cityregion", "CustomerAddress"));
            await context.SaveChangesAsync();

            var service = new SqlBuilderService(context);

            var group = new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = new List<SelectionRuleDto>
                {
                    new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "Wales" }
                }
            };

            var (_, join) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("LEFT JOIN CustomerAddresses", join);
        }

        // ✅ NESTED GROUP TESTS

        [Fact]
        public async Task NestedGroups_OrOperator_ReturnsCorrectClause()
        {
            var context = CreateContext();
            context.TreeNodes.Add(MakeNode(1, "Gender", "string"));
            context.TreeNodes.Add(MakeNode(2, "Age", "number"));
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
                            new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "Female" },
                            new SelectionRuleDto { TreeNodeId = 2, Operator = "=", Value = "65+" }
                        }
                    },
                    new SelectionGroupDto
                    {
                        LogicalOperator = "AND",
                        Rules = new List<SelectionRuleDto>
                        {
                            new SelectionRuleDto { TreeNodeId = 1, Operator = "=", Value = "Male" },
                            new SelectionRuleDto { TreeNodeId = 2, Operator = "=", Value = "18-24" }
                        }
                    }
                }
            };

            var (where, _) = await service.BuildQueryPartsAsync(group);

            Assert.Contains("OR", where);
            Assert.Contains("Female", where);
            Assert.Contains("Male", where);
            Assert.Contains(">=", where);
            Assert.Contains("BETWEEN", where);
        }

        // ✅ EMPTY RULES TEST

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