using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Helpers;
using crm_ai.Models;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace crm_ai.Services
{
    public class CampaignService : ICampaignService
    {
        private readonly AppDbContext _context;
        private readonly IAiService _aiService;
        private readonly ILogger<CampaignService> _logger;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        };

        public CampaignService(
            AppDbContext context,
            IAiService aiService,
            ILogger<CampaignService> logger)
        {
            _context = context;
            _aiService = aiService;
            _logger = logger;
        }

        // ════════════════════════════════════════════════════════════════════
        // MAIN CONVERSATION TURN
        // Engineering approach: extract ALL fields simultaneously, track
        // what is missing, ask ONE question, never overwrite confirmed fields
        // ════════════════════════════════════════════════════════════════════

        public async Task<CampaignConversationResponseDto> ContinueCampaignConversationAsync(
            CampaignConversationRequestDto request)
        {
            var draft = request.CurrentDraft ?? new CampaignDraftDto();
            var lastMessage = request.Messages
                .LastOrDefault(m => m.Role == "user")?.Content ?? "";

            _logger.LogInformation(
                "Campaign turn — message='{Msg}', draft={@Draft}, confirmed={C}",
                lastMessage.Length > 50 ? lastMessage[..50] + "..." : lastMessage,
                draft, request.Confirmed);

            // ── Step 1: Extract ALL available info from this message ──────
            // One AI call that fills in name, objective, channel simultaneously
            // Only overwrites null fields — never touches already-confirmed data
            if (!string.IsNullOrWhiteSpace(lastMessage))
            {
                draft = await ExtractAndMergeAsync(lastMessage, draft);
            }

            // ── Step 2: Find what is still missing ────────────────────────
            var missing = GetMissingFields(draft);

            _logger.LogInformation(
                "After extraction — missing fields: [{Missing}]",
                string.Join(", ", missing));

            // ── Step 3: User confirmed — save and return ──────────────────
            if (request.Confirmed && missing.Count == 0)
            {
                var saved = await SaveCampaignAsync(draft);
                draft.Id = saved.Id;

                return new CampaignConversationResponseDto
                {
                    Status = "completed",
                    Message = $"Campaign **{draft.Name}** created successfully! " +
                              $"Now let's build your audience and generate content.",
                    Draft = draft,
                    MissingFields = new(),
                    TokensUsed = 0
                };
            }

            // ── Step 4: All fields collected — ask for confirmation ───────
            if (missing.Count == 0)
            {
                return new CampaignConversationResponseDto
                {
                    Status = "confirming",
                    Message = PromptTemplates.Campaign.ConfirmationMessage(draft),
                    Draft = draft,
                    MissingFields = new(),
                    TokensUsed = 0
                };
            }

            // ── Step 5: Still missing fields — ask ONE question ───────────
            var (question, tokens) = await GetNextQuestionAsync(draft, missing);

            return new CampaignConversationResponseDto
            {
                Status = "collecting",
                Message = question,
                Draft = draft,
                MissingFields = missing,
                TokensUsed = tokens
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // EXTRACT AND MERGE — the engineering core of this service
        // ════════════════════════════════════════════════════════════════════

        private async Task<CampaignDraftDto> ExtractAndMergeAsync(
            string message, CampaignDraftDto current)
        {
            try
            {
                var (json, _) = await CallGroqAsync(
                    PromptTemplates.Campaign.ExtractionSystem,
                    PromptTemplates.Campaign.ExtractionUser(message),
                    maxTokens: 150);

                var extracted = JsonSerializer.Deserialize<CampaignDraftDto>(
                    CleanJson(json), _jsonOptions);

                if (extracted == null) return current;

                // Merge — ONLY fill nulls, never overwrite confirmed values
                // This is the key engineering decision: once a field is set
                // it stays set regardless of what future messages contain
                return new CampaignDraftDto
                {
                    Id = current.Id,
                    Name = current.Name ?? extracted.Name,
                    Objective = current.Objective ?? extracted.Objective,
                    Channel = current.Channel ?? extracted.Channel,
                    SelectionId = current.SelectionId,
                    SelectionName = current.SelectionName
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Field extraction failed — keeping current draft");
                return current;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // MISSING FIELDS CHECK
        // ════════════════════════════════════════════════════════════════════

        private static List<string> GetMissingFields(CampaignDraftDto draft)
        {
            var missing = new List<string>();
            // Priority order — channel is most important (drives content generation)
            if (string.IsNullOrWhiteSpace(draft.Channel)) missing.Add("channel");
            if (string.IsNullOrWhiteSpace(draft.Objective)) missing.Add("objective");
            if (string.IsNullOrWhiteSpace(draft.Name)) missing.Add("name");
            return missing;
        }

        // ════════════════════════════════════════════════════════════════════
        // NEXT QUESTION — ONE question about the most critical missing field
        // ════════════════════════════════════════════════════════════════════

        private async Task<(string Question, int Tokens)> GetNextQuestionAsync(
            CampaignDraftDto draft, List<string> missing)
        {
            try
            {
                var (json, tokens) = await CallGroqAsync(
                    PromptTemplates.Campaign.NextQuestionSystem,
                    PromptTemplates.Campaign.NextQuestionUser(draft, missing),
                    maxTokens: 100);

                var parsed = JsonSerializer.Deserialize<JsonElement>(
                    CleanJson(json), _jsonOptions);

                var question = parsed.TryGetProperty("question", out var q)
                    ? q.GetString() ?? BuildFallbackQuestion(missing[0])
                    : BuildFallbackQuestion(missing[0]);

                return (question, tokens);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Next question generation failed — using fallback");
                return (BuildFallbackQuestion(missing[0]), 0);
            }
        }

        private static string BuildFallbackQuestion(string missingField) =>
            missingField switch
            {
                "channel" => "Will this be an Email campaign or SMS?",
                "objective" => "What is the main goal of this campaign?",
                "name" => "What would you like to name this campaign?",
                _ => "Could you provide more details?"
            };

        // ════════════════════════════════════════════════════════════════════
        // PERSIST CAMPAIGN
        // ════════════════════════════════════════════════════════════════════

        private async Task<Campaign> SaveCampaignAsync(CampaignDraftDto draft)
        {
            // If campaign already exists (Id set), update it
            if (draft.Id.HasValue)
            {
                var existing = await _context.Campaigns.FindAsync(draft.Id.Value);
                if (existing != null)
                {
                    existing.Name = draft.Name ?? existing.Name;
                    existing.Objective = draft.Objective;
                    existing.Channel = draft.Channel ?? existing.Channel;
                    existing.SelectionId = draft.SelectionId;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    return existing;
                }
            }

            // Create new
            var campaign = new Campaign
            {
                Name = draft.Name ?? "Untitled Campaign",
                Objective = draft.Objective,
                Channel = draft.Channel ?? "Email",
                SelectionId = draft.SelectionId,
                Status = "Draft",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Campaigns.Add(campaign);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Campaign saved: Id={Id}, Name={Name}, Channel={Channel}",
                campaign.Id, campaign.Name, campaign.Channel);

            return campaign;
        }

        // ════════════════════════════════════════════════════════════════════
        // CRUD OPERATIONS
        // ════════════════════════════════════════════════════════════════════

        public async Task<List<CampaignSummaryDto>> GetAllCampaignsAsync()
        {
            return await _context.Campaigns
                .Include(c => c.Selection)
                .Include(c => c.Content)
                .Include(c => c.Schedule)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new CampaignSummaryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Objective = c.Objective,
                    Channel = c.Channel,
                    Status = c.Status,
                    SelectionName = c.Selection != null ? c.Selection.Name : null,
                    HasContent = c.Content != null,
                    HasSchedule = c.Schedule != null,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt
                })
                .ToListAsync();
        }

        public async Task<CampaignSummaryDto> GetCampaignByIdAsync(int id)
        {
            var campaign = await _context.Campaigns
                .Include(c => c.Selection)
                .Include(c => c.Content)
                .Include(c => c.Schedule)
                .FirstOrDefaultAsync(c => c.Id == id)
                ?? throw new KeyNotFoundException($"Campaign {id} not found");

            return new CampaignSummaryDto
            {
                Id = campaign.Id,
                Name = campaign.Name,
                Objective = campaign.Objective,
                Channel = campaign.Channel,
                Status = campaign.Status,
                SelectionName = campaign.Selection?.Name,
                HasContent = campaign.Content != null,
                HasSchedule = campaign.Schedule != null,
                CreatedAt = campaign.CreatedAt,
                UpdatedAt = campaign.UpdatedAt
            };
        }

        public async Task LinkSelectionAsync(int campaignId, int selectionId)
        {
            var campaign = await _context.Campaigns.FindAsync(campaignId)
                ?? throw new KeyNotFoundException($"Campaign {campaignId} not found");

            var selection = await _context.Selections.FindAsync(selectionId)
                ?? throw new KeyNotFoundException($"Selection {selectionId} not found");

            campaign.SelectionId = selectionId;
            campaign.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Selection {SelId} linked to Campaign {CamId}",
                selectionId, campaignId);
        }

        public async Task DeleteCampaignAsync(int id)
        {
            var campaign = await _context.Campaigns.FindAsync(id)
                ?? throw new KeyNotFoundException($"Campaign {id} not found");

            _context.Campaigns.Remove(campaign);
            await _context.SaveChangesAsync();
        }

        // ════════════════════════════════════════════════════════════════════
        // GROQ HELPER — reuses the same pattern as AiService
        // ════════════════════════════════════════════════════════════════════

        private readonly HttpClient _httpClient = null!;

        // NOTE: CampaignService calls AiService for Groq calls to avoid
        // duplicating the HTTP client setup. Add this to IAiService:
        // Task<(string Response, int Tokens)> CallAsync(string system, string user, int maxTokens);
        // OR inject HttpClient directly — see Step 8 below for the clean solution.

        private string CleanJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            var s = raw.Trim();
            if (s.StartsWith("```json")) s = s[7..];
            else if (s.StartsWith("```")) s = s[3..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
            var start = s.IndexOf('{');
            var end = s.LastIndexOf('}');
            return start >= 0 && end > start ? s[start..(end + 1)] : s;
        }

        private async Task<(string Response, int Tokens)> CallGroqAsync(
            string system, string user, int maxTokens = 200)
        {
            return await _aiService.CallPublicAsync(system, user, maxTokens);
        }
    }
}