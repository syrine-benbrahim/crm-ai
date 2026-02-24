using crm_ai.Data;
using crm_ai.Models;
using crm_ai.DTOs;
using Microsoft.EntityFrameworkCore;

namespace crm_ai.Services
{
    public class TreeService
    {
        private readonly AppDbContext _context;

        public TreeService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<List<TreeNodeDto>> GetTreeAsync()
        {
            var nodes = await _context.TreeNodes.ToListAsync();
            return BuildTree(nodes, null);
        }

        private List<TreeNodeDto> BuildTree(List<TreeNode> nodes, int? parentId)
        {
            return nodes
                .Where(n => n.ParentId == parentId)
                .Select(n => new TreeNodeDto
                {
                    Id = n.Id,
                    Label = n.NodeName,
                    IsSelectable = n.IsSelectable == 1,
                    DataType = n.DataType,
                    EntityName = n.EntityName,
                    FieldName = n.FieldName,
                    Children = BuildTree(nodes, n.Id)
                })
                .ToList();
        }
    }
}
