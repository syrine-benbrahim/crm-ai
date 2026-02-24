using crm_ai.Services;
using Microsoft.AspNetCore.Mvc;

namespace crm_ai.Controllers
{
    [ApiController]
    [Route("api/tree")]
    public class TreeController : ControllerBase
    {
        private readonly TreeService _treeService;

        public TreeController(TreeService treeService)
        {
            _treeService = treeService;
        }

        [HttpGet]
        public async Task<IActionResult> GetTree()
        {
            var result = await _treeService.GetTreeAsync();
            return Ok(result);
        }
    }
}
