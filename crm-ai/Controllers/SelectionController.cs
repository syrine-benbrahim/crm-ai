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
        private readonly IAiService _aiService;

        public SelectionController(ISelectionService service, IAiService aiService)
        {
            _service = service;
            _aiService = aiService;
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

        /// <summary>
        /// POST api/selection/generate-description
        /// Accepts a selection rule tree and returns an AI-generated
        /// plain-English audience description.
        /// </summary>
        [HttpPost("generate-description")]
        public async Task<IActionResult> GenerateDescription(
            [FromBody] AiDescriptionRequestDto dto)
        {
            if (dto?.RootGroup == null)
                return BadRequest(new { error = "RootGroup is required." });

            try
            {
                var result = await _aiService.GenerateSelectionDescriptionAsync(dto.RootGroup);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { error = ex.Message }); 
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Unexpected error.", detail = ex.Message });
            }
        }

        /// <summary>
        /// POST api/selection/generate-from-prompt
        /// User sends a plain-English description of their audience.
        /// AI returns a complete, ready-to-save SelectionRequestDto with real TreeNode IDs.
        /// Includes confidence scoring and unmatched term detection.
        /// </summary>
        [HttpPost("generate-from-prompt")]
        public async Task<IActionResult> GenerateFromPrompt(
            [FromBody] AiSelectionRequestDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.Prompt))
                return BadRequest(new { error = "Prompt is required." });

            if (dto.Prompt.Length > 500)
                return BadRequest(new { error = "Prompt must be under 500 characters." });

            try
            {
                var result = await _aiService.GenerateSelectionFromPromptAsync(
                    dto.Prompt,
                    dto.Name);

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error = "Unexpected error during AI selection generation.",
                    detail = ex.Message
                });
            }
        }
        /// <summary>
        /// POST api/selection/validate
        /// Validates a manually built rule tree for logical issues.
        /// Returns a plain-English summary + list of warnings/errors.
        /// Does NOT save anything — purely analytical.
        /// </summary>
        [HttpPost("validate")]
        public async Task<IActionResult> ValidateSelection(
            [FromBody] AiValidationRequestDto dto)
        {
            if (dto?.RootGroup == null)
                return BadRequest(new { error = "RootGroup is required." });

            try
            {
                var result = await _aiService.ValidateSelectionAsync(dto.RootGroup);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Unexpected error.", detail = ex.Message });
            }
        }

        /// <summary>
        /// POST api/selection/conversation
        /// Multi-turn conversational AI selection builder.
        /// Frontend sends the FULL conversation history every turn.
        /// Backend is completely stateless — no sessions, no DB storage.
        /// Returns status="clarifying" (needs more info) or status="completed" (built selection).
        /// </summary>
        [HttpPost("conversation")]
        public async Task<IActionResult> Conversation(
            [FromBody] ConversationRequestDto dto)
        {
            if (dto?.Messages == null || !dto.Messages.Any())
                return BadRequest(new { error = "Messages are required." });

            if (dto.Messages.Count > 20)
                return BadRequest(new { error = "Conversation too long. Please start a new session." });

            var lastMessage = dto.Messages.LastOrDefault(m => m.Role == "user");
            if (lastMessage == null)
                return BadRequest(new { error = "At least one user message is required." });

            if (lastMessage.Content?.Length > 1000)
                return BadRequest(new { error = "Message too long." });

            try
            {
                var result = await _aiService.ContinueConversationAsync(dto);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Unexpected error.", detail = ex.Message });
            }
        }
        [HttpPost("check-intent")]
        public async Task<IActionResult> CheckIntent([FromBody] IntentCheckRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(request.Intent))
                return BadRequest("Intent is required.");

            var result = await _aiService.CheckIntentAsync(request);
            return Ok(result);
        }
    }
}