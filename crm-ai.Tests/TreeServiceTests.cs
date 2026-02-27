using crm_ai.Data;
using crm_ai.Models;
using crm_ai.Services;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace crm_ai.Tests
{
    public class TreeServiceTests
    {
        private AppDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new AppDbContext(options);
        }

        [Fact]
        public async Task GetTreeAsync_ReturnsRootNodes_WithChildren()
        {
            var context = CreateContext();

            context.TreeNodes.AddRange(
                new TreeNode { Id = 1, NodeCode = "ROOT", NodeName = "Root", ParentId = null, IsSelectable = 0 },
                new TreeNode { Id = 2, NodeCode = "CHILD", NodeName = "Child", ParentId = 1, IsSelectable = 1, FieldName = "Email", DataType = "string" }
            );
            await context.SaveChangesAsync();

            var service = new TreeService(context);
            var result = await service.GetTreeAsync();

            Assert.Single(result);
            Assert.Single(result[0].Children);
            Assert.Equal("Child", result[0].Children[0].Label);
        }

        [Fact]
        public async Task GetTreeAsync_EmptyDatabase_ReturnsEmptyList()
        {
            var context = CreateContext();
            var service = new TreeService(context);

            var result = await service.GetTreeAsync();

            Assert.Empty(result);
        }

        [Fact]
        public async Task GetTreeAsync_SelectableNode_MapsIsSelectableCorrectly()
        {
            var context = CreateContext();
            context.TreeNodes.Add(new TreeNode
            {
                Id = 1,
                NodeCode = "N1",
                NodeName = "Node",
                ParentId = null,
                IsSelectable = 1
            });
            await context.SaveChangesAsync();

            var service = new TreeService(context);
            var result = await service.GetTreeAsync();

            Assert.True(result[0].IsSelectable);
        }
    }
}
