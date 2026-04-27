using crm_ai.DTOs;
using crm_ai.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace crm_ai.Controllers
{
    [ApiController]
    [Route("api/campaigns")]
    public class CampaignController : ControllerBase
    {
        private readonly ICampaignService _service;

        public CampaignController(ICampaignService service)
        {
            _service = service;
        }

        /// <summary>
        /// POST api/campaigns/conversation
        /// Main campaign creation conversation turn.
        /// Frontend sends full message history + current draft every turn.
        /// Backend is stateless — draft is owned by frontend.
        /// </summary>
        [HttpPost("conversation")]
        public async Task<IActionResult> Conversation(
            [FromBody] CampaignConversationRequestDto dto)
        {
            if (dto?.Messages == null || !dto.Messages.Any())
                return BadRequest(new { error = "Messages are required." });

            try
            {
                var result = await _service.ContinueCampaignConversationAsync(dto);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>GET api/campaigns</summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var result = await _service.GetAllCampaignsAsync();
            return Ok(result);
        }

        /// <summary>GET api/campaigns/{id}</summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            try
            {
                var result = await _service.GetCampaignByIdAsync(id);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        /// <summary>
        /// POST api/campaigns/{id}/link-selection
        /// Links a saved selection to an existing campaign draft.
        /// </summary>
        [HttpPost("{id}/link-selection")]
        public async Task<IActionResult> LinkSelection(
            int id, [FromBody] LinkSelectionDto dto)
        {
            try
            {
                await _service.LinkSelectionAsync(id, dto.SelectionId);
                return Ok(new { message = "Selection linked successfully." });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>DELETE api/campaigns/{id}</summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                await _service.DeleteCampaignAsync(id);
                return Ok(new { message = "Campaign deleted." });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}