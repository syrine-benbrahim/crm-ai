using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Models;
using crm_ai.Services;
using crm_ai.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace crm_ai.Controllers
{
    [ApiController]
    [Route("api/campaigns")]
    public class CampaignController : ControllerBase
    {
        private readonly ICampaignService _service;
        private readonly ICampaignWizardService _wizard;
        private readonly ILogger<CampaignController> _logger;
        private readonly ITemplateRenderingService _templateEngine;
        private readonly AppDbContext _context;
        private readonly IAiService _aiService;
        private readonly ITemplateRecommendationService _templateRecommendation;

        public CampaignController(
            ICampaignService service,
            ICampaignWizardService wizard,
            ITemplateRenderingService templateEngine,
            AppDbContext context,
            IAiService aiService,
            ILogger<CampaignController> logger,
            ITemplateRecommendationService templateRecommendation)
        {
            _service = service;
            _wizard = wizard;
            _templateEngine = templateEngine;
            _context = context;
            _aiService = aiService;
            _logger = logger;
            _templateRecommendation = templateRecommendation;
        }

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

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var result = await _service.GetAllCampaignsAsync();
            return Ok(result);
        }

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

        [HttpPost("{id}/strategy")]
        public async Task<IActionResult> GenerateStrategy(
            int id, [FromBody] GenerateStrategyRequestDto dto)
        {
            if (dto?.RootGroup == null)
                return BadRequest(new { error = "RootGroup is required." });
            try
            {
                var result = await _service.GenerateStrategyAsync(id, dto);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("wizard")]
        public async Task<IActionResult> Wizard([FromBody] WizardRequestDto dto)
        {
            if (dto?.Messages == null || !dto.Messages.Any())
                return BadRequest(new { error = "Messages are required." });
            try
            {
                var result = await _wizard.TurnAsync(dto);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Wizard error");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("templates")]
        public async Task<IActionResult> GetTemplates(
            [FromQuery] string? campaignType,
            [FromQuery] string? tone,
            [FromQuery] string? channel)
        {
            var all = await _templateEngine.GetAllTemplatesAsync();

            string? recommendedId = null;
            if (!string.IsNullOrWhiteSpace(campaignType) &&
                !string.IsNullOrWhiteSpace(tone) &&
                !string.IsNullOrWhiteSpace(channel))
            {
                var recommended = await _templateEngine
                    .RecommendAsync(campaignType, tone, channel);
                recommendedId = recommended?.Id;
            }

            var dtos = all
                .Where(t => channel == null ||
                    t.SupportedChannels.Contains(channel,
                        StringComparer.OrdinalIgnoreCase))
                .Select(t => new TemplateSchemaDto
                {
                    Id = t.Id,
                    Version = t.Version,
                    Name = t.Name,
                    Description = t.Description,
                    PreviewImageUrl = t.PreviewImageUrl,
                    SupportedObjectives = t.SupportedObjectives,
                    SupportedTones = t.SupportedTones,
                    SupportedChannels = t.SupportedChannels,
                    Slots = t.Slots.ToDictionary(
                        s => s.Key,
                        s => new SlotDefinitionDto
                        {
                            Type = s.Value.Type,
                            Required = s.Value.Required,
                            MaxLength = s.Value.MaxLength,
                            Label = s.Value.Label
                        }),
                    IsRecommended = t.Id == recommendedId,
                    RecommendationReason = t.Id == recommendedId
                        ? $"Best fit for {tone} tone and {campaignType} campaign"
                        : null
                })
                .OrderByDescending(t => t.IsRecommended)
                .ToList();

            return Ok(new { templates = dtos, recommendedTemplateId = recommendedId });
        }

        [HttpPost("templates/render")]
        public async Task<IActionResult> RenderTemplate([FromBody] RenderRequestDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.TemplateId))
                return BadRequest(new { error = "TemplateId is required." });

            try
            {
                var result = await _templateEngine.RenderAsync(dto.TemplateId, dto.Slots);
                var schema = await _templateEngine.GetTemplateAsync(dto.TemplateId);

                return Ok(new RenderResponseDto
                {
                    Success = result.Success,
                    Html = result.Html,
                    TemplateId = dto.TemplateId,
                    TemplateVersion = schema?.Version ?? 0,
                    Issues = result.Issues.Select(i => new RenderValidationIssueDto
                    {
                        Severity = i.Severity,
                        Slot = i.Slot,
                        Message = i.Message
                    }).ToList(),
                    AppliedSlots = result.AppliedSlots
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Render error for template {Id}", dto.TemplateId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("{id}/preview-html")]
        public async Task<IActionResult> PreviewHtml(int id)
        {
            try
            {
                var content = await _context.Set<CampaignContent>()
                    .FirstOrDefaultAsync(c => c.CampaignId == id);

                if (content == null)
                    return NotFound(new { error = "No content generated yet." });

                return Content(content.HtmlBody ?? "", "text/html");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Campaign execution ─────────────────────────────────────────────

        [HttpPost("{id}/run")]
        public async Task<IActionResult> Run(int id)
        {
            try
            {
                var result = await _service.ExecuteCampaignAsync(id);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Execution error for campaign {Id}", id);
                return StatusCode(500, new { error = ex.Message });
            }
        }

         [HttpPost("suggest-selections")]
          public async Task<IActionResult> SuggestSelections(
              [FromBody] SuggestSelectionsRequestDto request)
          {
            var result = await _service.SuggestSelectionsAsync(request);
              return Ok(result);
          }

        // ── Pre-launch simulation ──────────────────────────────────────────

        [HttpPost("{id}/simulate")]
        public async Task<IActionResult> Simulate(int id)
        {
            try
            {
                var result = await _service.SimulateCampaignAsync(id);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Simulation error for campaign {Id}", id);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Post-campaign AI analysis ──────────────────────────────────────
        // Fast model — interpreting numbers into recommendations
        // Not complex reasoning — fast model is sufficient and cheaper

        [HttpPost("{id}/analyse")]
        public async Task<IActionResult> AnalyseResults(int id)
        {
            try
            {
                var campaign = await _context.Campaigns
                    .FirstOrDefaultAsync(c => c.Id == id);

                if (campaign == null)
                    return NotFound(new { error = $"Campaign {id} not found" });

                if (campaign.Status != "Completed")
                    return BadRequest(new { error = "Campaign has not completed yet" });

                var openRate = campaign.TotalReach > 0
                    ? Math.Round((double)campaign.Opened / campaign.TotalReach * 100, 1)
                    : 0;

                var clickRate = campaign.TotalReach > 0
                    ? Math.Round((double)campaign.Clicked / campaign.TotalReach * 100, 1)
                    : 0;

                var (analysisJson, _) = await _aiService.CallPublicAsync(
                    """
                    You are a marketing analyst.
                    Analyse campaign results and give actionable recommendations.
                    Return ONLY valid JSON:
                    {
                      "whatWorked": ["...", "..."],
                      "whatToImprove": ["...", "..."],
                      "suggestedNextAction": "..."
                    }
                    Keep each string under 100 characters.
                    Provide 1-3 items per list.
                    """,
                    $"""
                    Campaign: {campaign.Name}
                    Channel: {campaign.Channel}
                    Objective: {campaign.Objective}
                    Audience: {campaign.TotalReach:N0} customers
                    Delivered: {campaign.Delivered}
                    Opened: {campaign.Opened} ({openRate}%)
                    Clicked: {campaign.Clicked} ({clickRate}%)
                    Industry averages: open 15%, click 2.8%
                    """,
                    maxTokens: 300,
                    model: _aiService.FastModel);

                try
                {
                    var cleaned = analysisJson.Trim()
                        .Replace("```json", "").Replace("```", "").Trim();
                    var start = cleaned.IndexOf('{');
                    var end = cleaned.LastIndexOf('}');
                    if (start >= 0 && end > start)
                        cleaned = cleaned[start..(end + 1)];

                    var analysis = JsonSerializer.Deserialize<CampaignAnalysisDto>(
                        cleaned, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                    return Ok(analysis);
                }
                catch
                {
                    return Ok(new CampaignAnalysisDto
                    {
                        WhatWorked = new List<string>
                        {
                            "Campaign reached target audience successfully"
                        },
                        WhatToImprove = new List<string>
                        {
                            "Consider adding a specific offer next time"
                        },
                        SuggestedNextAction =
                            "Build a follow-up audience targeting customers " +
                            "who opened but did not click"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analysis error for campaign {Id}", id);
                return StatusCode(500, new { error = ex.Message });
            }
        }
        [HttpPost("{id}/recommend-template")]
        public async Task<IActionResult> RecommendTemplate(
    int id,
    [FromBody] RecommendTemplateRequestDto request)
        {
            var result = await _templateRecommendation.RecommendAsync(
                objective: request.Objective,
                channel: request.Channel,
                selectionDescription: request.SelectionDescription);

            return Ok(result);
        }
    }
}