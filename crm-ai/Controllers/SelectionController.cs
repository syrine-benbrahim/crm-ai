using crm_ai.DTOs;
using crm_ai.Services;
using Microsoft.AspNetCore.Mvc;

namespace crm_ai.Controllers
{
    [ApiController]
    [Route("api/selection")]
    public class SelectionController : ControllerBase
    {
        private readonly SelectionService _service;

        public SelectionController(SelectionService service)
        {
            _service = service;
        }

        [HttpPost]
        public async Task<IActionResult> Create(
            [FromBody] SelectionRequestDto dto)
        {
            var id = await _service.CreateSelection(dto);
            return Ok(new { SelectionId = id });
        }

        [HttpPost("preview")]
        public async Task<IActionResult> Preview([FromBody] SelectionRequestDto dto)
        {
            try
            {
                var result = await _service.PreviewSelection(dto);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message, detail = ex.InnerException?.Message });
            }
        }

        [HttpPost("{id}/execute")]
public async Task<IActionResult> ExecuteSelection(int id)
{
    try
    {
        var result = await _service.ExecuteSelection(id);
        return Ok(result);
    }
    catch (Exception ex)
    {
        // ✅ This will show the real error
        return StatusCode(500, new { error = ex.Message, detail = ex.InnerException?.Message });
    }
}

        [HttpGet("{id}/executions")]
        public async Task<IActionResult> GetExecutions(int id)
        {
            var executions = await _service.GetExecutions(id);
            return Ok(executions);
        }
    }
}
