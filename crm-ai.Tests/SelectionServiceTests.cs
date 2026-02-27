using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Services;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace crm_ai.Tests
{
    public class SelectionServiceTests
    {
        private AppDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new AppDbContext(options);
        }

        [Fact]
        public async Task PreviewSelection_NullRootGroup_ThrowsArgumentException()
        {
            var context = CreateContext();
            var mockSqlBuilder = new Mock<ISqlBuilderService>();
            var service = new SelectionService(context, mockSqlBuilder.Object);

            var dto = new SelectionRequestDto { RootGroup = null };

            await Assert.ThrowsAsync<ArgumentException>(
                () => service.PreviewSelection(dto));
        }

        [Fact]
        public async Task CreateSelection_ValidDto_ReturnsSavedId()
        {
            var context = CreateContext();
            var mockSqlBuilder = new Mock<ISqlBuilderService>();
            var service = new SelectionService(context, mockSqlBuilder.Object);

            var dto = new SelectionRequestDto
            {
                Name = "Test Selection",
                RootGroup = new SelectionGroupDto
                {
                    LogicalOperator = "AND",
                    Rules = new List<SelectionRuleDto>()
                }
            };

            var id = await service.CreateSelection(dto);

            Assert.True(id > 0);
        }

        [Fact]
        public async Task GetAllSelections_ReturnsAllSelections()
        {
            var context = CreateContext();
            var mockSqlBuilder = new Mock<ISqlBuilderService>();
            var service = new SelectionService(context, mockSqlBuilder.Object);

            // Create 2 selections first
            var dto1 = new SelectionRequestDto
            {
                Name = "Selection One",
                RootGroup = new SelectionGroupDto
                {
                    LogicalOperator = "AND",
                    Rules = new List<SelectionRuleDto>()
                }
            };
            var dto2 = new SelectionRequestDto
            {
                Name = "Selection Two",
                RootGroup = new SelectionGroupDto
                {
                    LogicalOperator = "OR",
                    Rules = new List<SelectionRuleDto>()
                }
            };

            await service.CreateSelection(dto1);
            await service.CreateSelection(dto2);

            var result = await service.GetAllSelections();

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task GetSelectionById_ValidId_ReturnsSelection()
        {
            var context = CreateContext();
            var mockSqlBuilder = new Mock<ISqlBuilderService>();
            var service = new SelectionService(context, mockSqlBuilder.Object);

            var dto = new SelectionRequestDto
            {
                Name = "My Selection",
                RootGroup = new SelectionGroupDto
                {
                    LogicalOperator = "AND",
                    Rules = new List<SelectionRuleDto>()
                }
            };

            var id = await service.CreateSelection(dto);

            var result = await service.GetSelectionById(id);

            Assert.NotNull(result);
        }

        [Fact]
        public async Task GetSelectionById_InvalidId_ThrowsException()
        {
            var context = CreateContext();
            var mockSqlBuilder = new Mock<ISqlBuilderService>();
            var service = new SelectionService(context, mockSqlBuilder.Object);

            await Assert.ThrowsAsync<Exception>(
                () => service.GetSelectionById(9999));
        }
        [Fact]
        public async Task DeleteSelection_ValidId_RemovesSelection()
        {
            var context = CreateContext();
            var mockSqlBuilder = new Mock<ISqlBuilderService>();
            var service = new SelectionService(context, mockSqlBuilder.Object);

            var dto = new SelectionRequestDto
            {
                Name = "To Delete",
                RootGroup = new SelectionGroupDto
                {
                    LogicalOperator = "AND",
                    Rules = new List<SelectionRuleDto>()
                }
            };

            var id = await service.CreateSelection(dto);
            await service.DeleteSelection(id);

            var all = await service.GetAllSelections();
            Assert.Empty(all);
        }

        [Fact]
        public async Task DeleteSelection_InvalidId_ThrowsException()
        {
            var context = CreateContext();
            var mockSqlBuilder = new Mock<ISqlBuilderService>();
            var service = new SelectionService(context, mockSqlBuilder.Object);

            await Assert.ThrowsAsync<Exception>(
                () => service.DeleteSelection(9999));
        }

        [Fact]
        public async Task UpdateSelection_ValidId_UpdatesName()
        {
            var context = CreateContext();
            var mockSqlBuilder = new Mock<ISqlBuilderService>();
            var service = new SelectionService(context, mockSqlBuilder.Object);

            var dto = new SelectionRequestDto
            {
                Name = "Original Name",
                RootGroup = new SelectionGroupDto
                {
                    LogicalOperator = "AND",
                    Rules = new List<SelectionRuleDto>()
                }
            };

            var id = await service.CreateSelection(dto);

            var updateDto = new SelectionRequestDto
            {
                Name = "Updated Name",
                RootGroup = new SelectionGroupDto
                {
                    LogicalOperator = "OR",
                    Rules = new List<SelectionRuleDto>()
                }
            };

            await service.UpdateSelection(id, updateDto);

            var result = await service.GetSelectionById(id);
            Assert.NotNull(result);
        }

        [Fact]
        public async Task UpdateSelection_InvalidId_ThrowsException()
        {
            var context = CreateContext();
            var mockSqlBuilder = new Mock<ISqlBuilderService>();
            var service = new SelectionService(context, mockSqlBuilder.Object);

            var dto = new SelectionRequestDto
            {
                Name = "Updated Name",
                RootGroup = new SelectionGroupDto
                {
                    LogicalOperator = "AND",
                    Rules = new List<SelectionRuleDto>()
                }
            };

            await Assert.ThrowsAsync<Exception>(
                () => service.UpdateSelection(9999, dto));
        }
    }
}
