using crm_ai.DTOs;
using crm_ai.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace crm_ai.Controllers
{
    [ApiController]
    [Route("api/selection")]
    public class SelectionController : ControllerBase
    {
        private readonly ISelectionService _service;

        public SelectionController(ISelectionService service)
        {
            _service = service;
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] SelectionRequestDto dto)
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
                return StatusCode(500, new { error = ex.Message, detail = ex.InnerException?.Message });
            }
        }

        [HttpGet("{id}/executions")]
        public async Task<IActionResult> GetExecutions(int id)
        {
            var executions = await _service.GetExecutions(id);
            return Ok(executions);
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var result = await _service.GetAllSelections();
            return Ok(result);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            try
            {
                var result = await _service.GetSelectionById(id);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                await _service.DeleteSelection(id);
                return Ok(new { message = "Selection deleted successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] SelectionRequestDto dto)
        {
            try
            {
                await _service.UpdateSelection(id, dto);
                return Ok(new { message = "Selection updated successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}