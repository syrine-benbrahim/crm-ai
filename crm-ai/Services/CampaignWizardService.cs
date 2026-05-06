using crm_ai.Data;
using crm_ai.DTOs;
using crm_ai.Helpers;
using crm_ai.Models;
using crm_ai.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace crm_ai.Services
{
    public class CampaignWizardService : ICampaignWizardService
    {
        private readonly AppDbContext _context;
        private readonly IAiService _aiService;
        private readonly ISelectionService _selectionService;
        private readonly SegmentProfileBuilder _segmentProfileBuilder;
        private readonly ILogger<CampaignWizardService> _logger;
        private readonly ITemplateRenderingService _templateEngine;
        private readonly ITemplateRecommendationService _templateRecommendation;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        };

        public CampaignWizardService(
            AppDbContext context,
            IAiService aiService,
            ISelectionService selectionService,
            SegmentProfileBuilder segmentProfileBuilder,
            ITemplateRenderingService templateEngine,
            ITemplateRecommendationService templateRecommendation,
            ILogger<CampaignWizardService> logger)
        {
            _context = context;
            _aiService = aiService;
            _selectionService = selectionService;
            _segmentProfileBuilder = segmentProfileBuilder;
            _templateEngine = templateEngine;
            _logger = logger;
            _templateRecommendation = templateRecommendation;
        }

        public async Task<WizardResponseDto> TurnAsync(WizardRequestDto request)
        {
            var state = request.State ?? new WizardStateDto();
            var lastMessage = request.Messages
                .LastOrDefault(m => m.Role == "user")?.Content ?? "";

            _logger.LogInformation(
                "Wizard turn — phase={Phase}, message='{Msg}'",
                state.Phase,
                lastMessage.Length > 60 ? lastMessage[..60] + "..." : lastMessage);

            return state.Phase switch
            {
                "collecting" => await HandleCollectingAsync(lastMessage, state, request.Messages),
                "suggest_selection" => await HandleSuggestSelectionAsync(lastMessage, state),
                "building_selection" => await HandleBuildingSelectionAsync(lastMessage, state, request.Messages),
                "strategy" => await HandleStrategyAsync(lastMessage, state),
                "suggest_template" => await HandleSuggestTemplateAsync(lastMessage, state),
                "generating_content" => await HandleGeneratingContentAsync(lastMessage, state),
                "suggest_schedule" => await HandleSuggestScheduleAsync(lastMessage, state),
                "confirming" => await HandleConfirmingAsync(lastMessage, state),
                _ => await HandleCollectingAsync(lastMessage, state, request.Messages)
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PHASE 1 — COLLECTING
        // Fast model — extraction returns 3 fields, simple structured output
        // Fast model — question generation returns 1 short question
        // ════════════════════════════════════════════════════════════════════

        private async Task<WizardResponseDto> HandleCollectingAsync(
            string message,
            WizardStateDto state,
            List<ConversationMessage> messages)
        {
            var draft = state.CampaignDraft ?? new CampaignDraftDto();

            if (!string.IsNullOrWhiteSpace(message))
            {
                var (extractedJson, _) = await CallFastAsync(
                    PromptTemplates.Campaign.ExtractionSystem,
                    PromptTemplates.Campaign.ExtractionUser(message),
                    maxTokens: 150);

                try
                {
                    var extracted = JsonSerializer.Deserialize<CampaignDraftDto>(
                        CleanJson(extractedJson), _jsonOptions);

                    if (extracted != null)
                    {
                        draft.Name = draft.Name ?? extracted.Name;
                        draft.Objective = draft.Objective ?? extracted.Objective;
                        draft.Channel = draft.Channel ?? extracted.Channel;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Extraction parse failed");
                }
            }

            state.CampaignDraft = draft;

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(draft.Channel)) missing.Add("channel");
            if (string.IsNullOrWhiteSpace(draft.Objective)) missing.Add("objective");
            if (string.IsNullOrWhiteSpace(draft.Name)) missing.Add("name");

            if (missing.Count == 0)
            {
                state.Phase = "suggest_selection";
                return await HandleSuggestSelectionAsync("", state);
            }

            var (questionJson, tokens) = await CallFastAsync(
                PromptTemplates.Campaign.NextQuestionSystem,
                PromptTemplates.Campaign.NextQuestionUser(draft, missing),
                maxTokens: 100);

            var question = TryGetString(questionJson, "question")
                ?? BuildFallbackQuestion(missing[0]);

            return new WizardResponseDto
            {
                Phase = "collecting",
                Message = question,
                State = state,
                TokensUsed = tokens
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PHASE 2 — SUGGEST SELECTION
        // Fast model — returns single index integer, simple match task
        // ════════════════════════════════════════════════════════════════════

        private async Task<WizardResponseDto> HandleSuggestSelectionAsync(
            string message,
            WizardStateDto state)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                var lower = message.ToLower();

                if (lower.Contains("new") || lower.Contains("build") ||
                    lower.Contains("create") || lower.Contains("none"))
                {
                    state.Phase = "building_selection";
                    return new WizardResponseDto
                    {
                        Phase = "building_selection",
                        Message = "Sure — describe the audience you want to target. " +
                                  "For example: 'female customers in London aged 25-44 " +
                                  "who haven't visited in 3 months'",
                        State = state
                    };
                }

                var selections = await _context.Selections
                    .Where(s => s.Status == "Active")
                    .OrderByDescending(s => s.CreatedAt)
                    .Take(10)
                    .ToListAsync();

                var picked = TryPickSelection(message, selections);

                if (picked != null)
                {
                    state.SelectionId = picked.Id;
                    state.SelectionName = picked.Name;
                    state.SelectionDescription = picked.Description ?? picked.Name;
                    state.Phase = "strategy";
                    return await HandleStrategyAsync("", state);
                }
            }

            var allSelections = await _context.Selections
                .Where(s => s.Status == "Active")
                .OrderByDescending(s => s.CreatedAt)
                .Take(8)
                .ToListAsync();

            if (allSelections.Count == 0)
            {
                state.Phase = "building_selection";
                return new WizardResponseDto
                {
                    Phase = "building_selection",
                    Message = "You don't have any saved selections yet. " +
                              "Let's build one. Describe your target audience:",
                    State = state
                };
            }

            var recommendedId = await RecommendSelectionAsync(
                state.CampaignDraft.Objective ?? "", allSelections);

            var suggestions = allSelections.Select((s, i) => new WizardSelectionSuggestionDto
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                CustomerCount = 0,
                IsRecommended = s.Id == recommendedId,
                RecommendationReason = s.Id == recommendedId
                    ? "Best match for your objective"
                    : null
            }).OrderByDescending(s => s.IsRecommended).ToList();

            var recommended = allSelections.FirstOrDefault(s => s.Id == recommendedId);
            var recommendedName = recommended?.Name ?? allSelections.First().Name;

            return new WizardResponseDto
            {
                Phase = "suggest_selection",
                Message = $"I found {allSelections.Count} existing selections. " +
                          $"I recommend **{recommendedName}** based on your objective. " +
                          $"Pick one or say **'build new'** to create a custom audience:",
                SelectionSuggestions = suggestions,
                State = state
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PHASE 3 — BUILDING SELECTION
        // Delegates entirely to existing ContinueConversationAsync
        // No model choice needed here — AiService handles it internally
        // ════════════════════════════════════════════════════════════════════

        private async Task<WizardResponseDto> HandleBuildingSelectionAsync(
            string message,
            WizardStateDto state,
            List<ConversationMessage> messages)
        {
            var convRequest = new ConversationRequestDto
            {
                Messages = messages,
                CurrentRootGroup = state.SelectionCurrentRootGroup,
                IntentConfirmed = state.SelectionIntentConfirmed,
                Confirmed = false,
                Name = null
            };

            var lower = message.ToLower().Trim();
            if (lower is "yes" or "confirm" or "save it" or "use it" or "looks good" or "ok" or "okay"
                && state.SelectionCurrentRootGroup != null)
            {
                convRequest.Confirmed = true;
            }

            var convResult = await _aiService.ContinueConversationAsync(convRequest);

            if (convResult.Selection?.RootGroup != null)
                state.SelectionCurrentRootGroup = convResult.Selection.RootGroup;

            if (convResult.Status == "intent_confirmation")
                state.SelectionIntentConfirmed = false;

            if (convResult.Status == "pending_confirmation")
                state.SelectionIntentConfirmed = true;

            if (convResult.Status == "completed" && convResult.Selection != null)
            {
                var savedId = await SaveSelectionAsync(
                    convResult.Selection,
                    state.CampaignDraft.Name ?? "Wizard Selection");

                state.SelectionId = savedId;
                state.SelectionName = convResult.Selection.Name;
                state.SelectionDescription = convResult.Selection.Description;
                state.SelectionRootGroup = convResult.Selection.RootGroup;
                state.SelectionCurrentRootGroup = null;
                state.SelectionIntentConfirmed = null;
                state.Phase = "strategy";

                return await HandleStrategyAsync("", state);
            }

            return new WizardResponseDto
            {
                Phase = "building_selection",
                Message = convResult.Message,
                Clarifications = convResult.Clarifications ?? [],
                ClarificationStateId = convResult.ClarificationStateId,
                State = state,
                TokensUsed = convResult.TokensUsed
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PHASE 4 — STRATEGY
        // Power model — complex reasoning across profile dimensions
        // Same justification as selection building in AiService
        // ════════════════════════════════════════════════════════════════════

        private async Task<WizardResponseDto> HandleStrategyAsync(
            string message,
            WizardStateDto state)
        {
            if (!string.IsNullOrWhiteSpace(state.CampaignType))
            {
                state.Phase = "suggest_template";
                return await HandleSuggestTemplateAsync("", state);
            }

            SelectionGroupDto rootGroup = state.SelectionRootGroup ?? new SelectionGroupDto
            {
                LogicalOperator = "AND",
                Rules = [],
                Groups = []
            };

            if (state.SelectionId.HasValue && state.SelectionRootGroup == null)
                rootGroup = await LoadSelectionRootGroupAsync(state.SelectionId.Value);

            var catalog = await _aiService.BuildNodeCatalogPublicAsync();
            var profile = await _segmentProfileBuilder.BuildAsync(
                rootGroup, catalog,
                state.SelectionDescription ?? state.SelectionName ?? "");

            var strategyRequest = new GenerateStrategyRequestDto
            {
                RootGroup = rootGroup,
                SelectionDescription = state.SelectionDescription ?? "",
                Channel = state.CampaignDraft.Channel ?? "Email",
                Objective = state.CampaignDraft.Objective ?? ""
            };

            var systemPrompt = BuildStrategySystemPrompt();
            var userPrompt = BuildStrategyUserPrompt(profile, strategyRequest);

            // Power model — strategy requires reasoning across engagement,
            // value tier, channel coverage to produce campaign type + tone decision
            var (strategyJson, tokens) = await CallPowerAsync(
                systemPrompt, userPrompt, maxTokens: 600);

            var (campaignType, tone, sendTime, explanation) = ParseStrategy(strategyJson);

            state.CampaignType = campaignType;
            state.Tone = tone;
            state.SegmentProfile = profile;
            state.StrategyExplanation = explanation;
            state.Phase = "suggest_template";

            var explanationText = string.Join("\n", explanation.Select(e =>
                $"• **{e.Signal}** — {e.Decision}"));

            return new WizardResponseDto
            {
                Phase = "strategy",
                Message = $"Here's the strategy I recommend for your campaign:\n\n" +
                          $"**Type:** {campaignType} | **Tone:** {tone}\n\n" +
                          $"**Why this strategy:**\n{explanationText}\n\n" +
                          $"Ready to pick a template?",
                StrategyExplanation = explanation,
                SegmentProfile = profile,
                State = state,
                TokensUsed = tokens
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PHASE 5 — SUGGEST TEMPLATE
        // Pure C# deterministic matching — zero AI tokens
        // ════════════════════════════════════════════════════════════════════

        private async Task<WizardResponseDto> HandleSuggestTemplateAsync(
    string message,
    WizardStateDto state)
        {
            var channel = state.CampaignDraft.Channel ?? "Email";
            var objective = state.CampaignDraft.Objective ?? "";
            var selectionDesc = state.SelectionDescription;

            // User is picking a template from the list shown last turn
            if (!string.IsNullOrWhiteSpace(message) && state.ChosenTemplateId == null)
            {
                // Load templates to resolve user's pick
                var result = await _templateRecommendation.RecommendAsync(
                    objective, channel, selectionDesc);

                var picked = TryPickTemplate(
                    message,
                    result.Templates,
                    result.RecommendedTemplateId);

                if (picked != null)
                {
                    state.ChosenTemplateId = picked;
                    state.Phase = "generating_content";
                    return await HandleGeneratingContentAsync("", state);
                }

                // User said something but we couldn't resolve a template —
                // check if they want manual build
                var lower = message.ToLower();
                if (lower.Contains("manual") || lower.Contains("build") ||
                    lower.Contains("custom") || lower.Contains("own"))
                {
                    state.ChosenTemplateId = "manual";
                    state.Phase = "generating_content";
                    return new WizardResponseDto
                    {
                        Phase = "generating_content",
                        Message = "No problem — you can write your content directly below. " +
                                  "Fill in the subject, headline, and body when you're ready.",
                        State = state
                    };
                }
            }

            // Generate recommendation
            var recommendation = await _templateRecommendation.RecommendAsync(
                objective, channel, selectionDesc);

            // Store detected type/tone in state so content generator can use it
            state.CampaignType ??= recommendation.CampaignType;
            state.Tone ??= recommendation.Tone;

            // RequiresManualBuild: no templates for this channel
            if (recommendation.RequiresManualBuild)
            {
                state.ChosenTemplateId = "manual";
                return new WizardResponseDto
                {
                    Phase = "suggest_template",
                    Message = recommendation.ManualBuildReason ??
                              "No templates are available for this channel. " +
                              "You can build your content manually below.",
                    State = state,
                    RequiresManualBuild = true
                };
            }

            var recommended = recommendation.Templates
                .FirstOrDefault(t => t.Id == recommendation.RecommendedTemplateId);

            return new WizardResponseDto
            {
                Phase = "suggest_template",
                Message = recommended != null
                    ? $"Based on your campaign, I recommend the **{recommended.Name}** template. " +
                      $"{recommended.RecommendationReason} " +
                      $"Pick one below or say **'{recommended.Name}'** to use the recommendation:"
                    : "Here are the available templates for your campaign. Pick one to continue:",
                Templates = recommendation.Templates
                    .Select(t => new TemplateMetadataDto
                    {
                        Id = t.Id,
                        Name = t.Name,
                        Description = t.Description,
                        PreviewImageUrl = t.PreviewImageUrl,
                        SupportedObjectives = t.SupportedObjectives,
                        SupportedTones = t.SupportedTones,
                        SupportedChannels = [channel],
                        ContentSlots = t.ContentSlots,
                        IsRecommended = t.IsRecommended,
                        RecommendationReason = t.RecommendationReason
                    }).ToList(),
                RecommendedTemplateId = recommendation.RecommendedTemplateId,
                State = state
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PHASE 6 — GENERATING CONTENT
        // Power model — creative generation requiring tone + audience awareness
        // ════════════════════════════════════════════════════════════════════

        private async Task<WizardResponseDto> HandleGeneratingContentAsync(
            string message,
            WizardStateDto state)
        {
            if (state.GeneratedContent != null &&
                !string.IsNullOrWhiteSpace(message) &&
                IsConfirmation(message))
            {
                state.Phase = "suggest_schedule";
                return await HandleSuggestScheduleAsync("", state);
            }

            var profile = state.SegmentProfile;
            var systemPrompt =
                """
                You are an expert email copywriter for retail marketing campaigns.
                Generate campaign content for the given audience and strategy.
                Match the tone exactly.
                Insert {first_name} where a personal greeting fits.
                Subject: max 50 chars. Preheader: max 90 chars.
                Hero headline: max 8 words. Body: 2-3 sentences each paragraph.
                CTA: 2-4 words, action verb.
                SMS: MUST be ≤160 chars, include brand name and call to action.

                Return ONLY valid JSON:
                {
                  "subject": "...",
                  "preheader": "...",
                  "hero_headline": "...",
                  "body_para_1": "...",
                  "body_para_2": "...",
                  "cta_text": "...",
                  "sms_text": "..."
                }
                """;

            var userPrompt =
                $"""
                AUDIENCE: {profile?.BehaviourSummary ?? state.SelectionDescription}
                CAMPAIGN TYPE: {state.CampaignType}
                TONE: {state.Tone}
                CHANNEL: {state.CampaignDraft.Channel}
                OBJECTIVE: {state.CampaignDraft.Objective}
                TEMPLATE: {state.ChosenTemplateId}

                Generate the campaign content.
                """;

            // Power model — content generation requires creative + tone awareness
            var (contentJson, tokens) = await CallPowerAsync(
                systemPrompt, userPrompt, maxTokens: 600);

            var content = ParseContent(contentJson);

            // Content length validation — deterministic C# guard
            // AI does not always respect character limits in the prompt
            if (content.Subject?.Length > 60)
                content.Subject = content.Subject[..60].TrimEnd() + "…";
            if (content.SmsText?.Length > 160)
                content.SmsText = content.SmsText[..157].TrimEnd() + "…";
            if (content.CtaText?.Length > 20)
                content.CtaText = content.CtaText[..20].TrimEnd();

            var templateId = state.ChosenTemplateId ?? "winback_urgency";
            var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["subject"] = content.Subject ?? "",
                ["preheader"] = content.Preheader ?? "",
                ["hero_headline"] = content.HeroHeadline ?? "",
                ["body_para_1"] = content.BodyPara1 ?? "",
                ["body_para_2"] = content.BodyPara2 ?? "",
                ["cta_text"] = content.CtaText ?? "",
                ["sms_text"] = content.SmsText ?? "",
                ["cta_url"] = "#",
                ["unsubscribe_url"] = "#",
                ["first_name"] = "{first_name}"
            };

            var renderResult = await _templateEngine.RenderAsync(templateId, slots);
            content.FinalHtml = renderResult.Html;

            foreach (var issue in renderResult.Issues)
                _logger.LogWarning(
                    "Template validation [{Severity}] slot={Slot}: {Message}",
                    issue.Severity, issue.Slot, issue.Message);

            state.GeneratedContent = content;
            state.Phase = "suggest_schedule";

            return new WizardResponseDto
            {
                Phase = "generating_content",
                Message = $"Here's your generated content:\n\n" +
                          $"**Subject:** {content.Subject}\n" +
                          $"**Headline:** {content.HeroHeadline}\n\n" +
                          $"{content.BodyPara1}\n\n" +
                          $"{content.BodyPara2}\n\n" +
                          $"**CTA:** {content.CtaText}\n\n" +
                          $"**SMS:** {content.SmsText}\n\n" +
                          "Does this look good, or say **'regenerate'** for a new version?",
                Content = content,
                State = state,
                TokensUsed = tokens
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PHASE 7 — SUGGEST SCHEDULE
        // Pure C# visit pattern analysis — zero AI tokens
        // ════════════════════════════════════════════════════════════════════

        private async Task<WizardResponseDto> HandleSuggestScheduleAsync(
            string message,
            WizardStateDto state)
        {
            if (!string.IsNullOrWhiteSpace(message) && IsConfirmation(message))
            {
                state.Phase = "confirming";
                return await HandleConfirmingAsync("", state);
            }

            if (!string.IsNullOrWhiteSpace(message) &&
                !message.ToLower().Contains("suggest") &&
                !message.ToLower().Contains("recommend"))
            {
                state.ScheduledAt = message;
                state.Phase = "confirming";
                return await HandleConfirmingAsync("", state);
            }

            var (recommendedDay, recommendedHour, reason) =
                await GetSendTimeAsync(state.SegmentProfile);

            var suggestedTime = $"{recommendedDay} at {recommendedHour}";
            state.ScheduledAt = suggestedTime;

            return new WizardResponseDto
            {
                Phase = "suggest_schedule",
                Message = $"Based on your audience's visit patterns, " +
                          $"I recommend sending on **{suggestedTime}**.\n\n" +
                          $"_{reason}_\n\n" +
                          $"Say **'yes'** to use this time, or tell me when you'd like to send it:",
                State = state
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PHASE 8 — CONFIRMING + COMPLETED
        // Pure C# — no AI involved in saving
        // ════════════════════════════════════════════════════════════════════

        private async Task<WizardResponseDto> HandleConfirmingAsync(
            string message,
            WizardStateDto state)
        {
            if (!string.IsNullOrWhiteSpace(message) && IsConfirmation(message))
            {
                await SaveCampaignAsync(state);
                state.Phase = "completed";

                return new WizardResponseDto
                {
                    Phase = "completed",
                    Message = $"✅ Campaign **{state.CampaignDraft.Name}** created successfully!\n\n" +
                              $"**Channel:** {state.CampaignDraft.Channel}\n" +
                              $"**Audience:** {state.SelectionName}\n" +
                              $"**Send time:** {state.ScheduledAt}\n" +
                              $"**Subject:** {state.GeneratedContent?.Subject}",
                    State = state
                };
            }

            var summary =
                $"Here's your campaign summary:\n\n" +
                $"📋 **Name:** {state.CampaignDraft.Name}\n" +
                $"🎯 **Objective:** {state.CampaignDraft.Objective}\n" +
                $"📣 **Channel:** {state.CampaignDraft.Channel}\n" +
                $"👥 **Audience:** {state.SelectionName}\n" +
                $"🧠 **Strategy:** {state.CampaignType} — {state.Tone} tone\n" +
                $"📧 **Subject:** {state.GeneratedContent?.Subject}\n" +
                $"⏰ **Send time:** {state.ScheduledAt}\n\n" +
                $"Say **'confirm'** to save and schedule, or tell me what to change:";

            return new WizardResponseDto
            {
                Phase = "confirming",
                Message = summary,
                Content = state.GeneratedContent,
                State = state
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PRIVATE HELPERS
        // ════════════════════════════════════════════════════════════════════

        private async Task<int> RecommendSelectionAsync(
            string objective,
            List<Selection> selections)
        {
            if (selections.Count == 0) return 0;
            if (selections.Count == 1) return selections[0].Id;

            try
            {
                var list = string.Join("\n", selections.Select((s, i) =>
                    $"{i + 1}. {s.Name} — {s.Description ?? "no description"}"));

                // Fast model — returns single integer index, simple matching task
                var (json, _) = await CallFastAsync(
                    "You help match campaign objectives to existing audience selections. " +
                    "Return ONLY a JSON object: {\"index\": 1} where index is 1-based.",
                    $"Campaign objective: \"{objective}\"\n\nSelections:\n{list}\n\n" +
                    "Which selection best matches? Return {{\"index\": N}}",
                    maxTokens: 20);

                var parsed = JsonSerializer.Deserialize<JsonElement>(CleanJson(json), _jsonOptions);
                if (parsed.TryGetProperty("index", out var idx))
                {
                    var i = idx.GetInt32() - 1;
                    if (i >= 0 && i < selections.Count)
                        return selections[i].Id;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Selection recommendation failed — using first");
            }

            return selections[0].Id;
        }

        private async Task<SelectionGroupDto> LoadSelectionRootGroupAsync(int selectionId)
        {
            try
            {
                var groups = await _context.SelectionGroups
                    .Include(g => g.Rules)
                    .Include(g => g.ChildGroups)
                    .Where(g => g.SelectionId == selectionId && g.ParentGroupId == null)
                    .FirstOrDefaultAsync();

                if (groups == null)
                    return new SelectionGroupDto { LogicalOperator = "AND", Rules = [], Groups = [] };

                return MapGroup(groups);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load selection root group for {Id}", selectionId);
                return new SelectionGroupDto { LogicalOperator = "AND", Rules = [], Groups = [] };
            }
        }

        private static SelectionGroupDto MapGroup(SelectionGroup group)
        {
            return new SelectionGroupDto
            {
                LogicalOperator = group.LogicalOperator,
                Rules = group.Rules?.Select(r => new SelectionRuleDto
                {
                    TreeNodeId = r.TreeNodeId,
                    Operator = r.Operator,
                    Value = r.Value
                }).ToList() ?? [],
                Groups = group.ChildGroups?.Select(MapGroup).ToList() ?? []
            };
        }

        private async Task<int> SaveSelectionAsync(
            AiSelectionResponseDto selection, string name)
        {
            var request = new SelectionRequestDto
            {
                Name = selection.Name ?? name,
                Description = selection.Description,
                RootGroup = selection.RootGroup
            };
            return await _selectionService.CreateSelection(request);
        }

        private async Task SaveCampaignAsync(WizardStateDto state)
        {
            var campaign = new Campaign
            {
                Name = state.CampaignDraft.Name ?? "Untitled Campaign",
                Objective = state.CampaignDraft.Objective,
                Channel = state.CampaignDraft.Channel ?? "Email",
                SelectionId = state.SelectionId,
                Status = "Draft",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.Campaigns.Add(campaign);
            await _context.SaveChangesAsync();

            if (state.GeneratedContent != null)
            {
                _context.Set<CampaignContent>().Add(new CampaignContent
                {
                    CampaignId = campaign.Id,
                    ContentType = state.CampaignDraft.Channel ?? "Email",
                    Subject = state.GeneratedContent.Subject,
                    HtmlBody = state.GeneratedContent.FinalHtml,
                    SmsText = state.GeneratedContent.SmsText,
                    GeneratedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
            }

            _logger.LogInformation("Campaign saved via wizard: Id={Id}", campaign.Id);
        }

        private async Task<(string Day, string Hour, string Reason)> GetSendTimeAsync(
            SegmentProfileDto? profile)
        {
            if (profile?.RecommendedSendDay != null)
                return (profile.RecommendedSendDay, profile.RecommendedSendHour ?? "12:00",
                    "Based on your audience's peak visit times.");

            try
            {
                var raw = await _context.Visits
                    .Select(v => new { v.VisitDateTime })
                    .ToListAsync();

                if (raw.Count > 0)
                {
                    var top = raw
                        .GroupBy(v => new
                        {
                            Day = v.VisitDateTime.DayOfWeek.ToString(),
                            Hour = v.VisitDateTime.Hour.ToString("00") + ":00"
                        })
                        .OrderByDescending(g => g.Count())
                        .First();

                    return (top.Key.Day, top.Key.Hour,
                        "Based on your audience's peak visit times.");
                }
            }
            catch { }

            return ("Thursday", "12:00", "Industry average for retail email campaigns.");
        }

        private static string RecommendTemplate(
            string campaignType, string tone, string channel,
            List<TemplateMetadataDto> templates)
        {
            var exact = templates.FirstOrDefault(t =>
                t.SupportedObjectives.Contains(campaignType, StringComparer.OrdinalIgnoreCase) &&
                t.SupportedTones.Contains(tone, StringComparer.OrdinalIgnoreCase));

            if (exact != null) return exact.Id;

            var byObjective = templates.FirstOrDefault(t =>
                t.SupportedObjectives.Contains(campaignType, StringComparer.OrdinalIgnoreCase));

            return byObjective?.Id ?? templates.FirstOrDefault()?.Id ?? "";
        }

        private static string? TryPickTemplate(
            string message,
            List<TemplateSuggestionDto> templates,         // ← changed type
            string? recommendedId)
        {
            var lower = message.ToLower();

            if (lower.Contains("recommend") || lower.Contains("that one") ||
                lower.Contains("yes") || lower.Contains("ok") || lower.Contains("use it"))
                return recommendedId;

            foreach (var t in templates)
                if (lower.Contains(t.Name.ToLower()) || lower.Contains(t.Id.ToLower()))
                    return t.Id;

            for (int i = 0; i < templates.Count; i++)
                if (lower.Contains($"{i + 1}"))
                    return templates[i].Id;

            return null;
        }

        private static Selection? TryPickSelection(string message, List<Selection> selections)
        {
            var lower = message.ToLower();

            foreach (var s in selections)
                if (lower.Contains(s.Name.ToLower()))
                    return s;

            for (int i = 0; i < selections.Count; i++)
                if (lower.Contains($"{i + 1}") || lower.Contains($"option {i + 1}"))
                    return selections[i];

            return null;
        }

        private static bool IsConfirmation(string message)
        {
            var confirmations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "yes", "confirm", "ok", "okay", "looks good", "perfect",
                "great", "save it", "use it", "go ahead", "sure", "proceed",
                "send it", "schedule it", "yes please", "that's good"
            };
            return confirmations.Contains(message.Trim()) ||
                   message.ToLower().StartsWith("yes") ||
                   message.ToLower().Contains("confirm");
        }

        private (string CampaignType, string Tone, string? SendTime,
            List<StrategyExplanationPointDto> Explanation)
            ParseStrategy(string raw)
        {
            try
            {
                var cleaned = CleanJson(raw);
                var parsed = JsonSerializer.Deserialize<JsonElement>(cleaned, _jsonOptions);

                var explanation = new List<StrategyExplanationPointDto>();
                if (parsed.TryGetProperty("explanation", out var arr))
                    foreach (var item in arr.EnumerateArray())
                        explanation.Add(new StrategyExplanationPointDto
                        {
                            Signal = TryGetStringEl(item, "signal"),
                            Implication = TryGetStringEl(item, "implication"),
                            Decision = TryGetStringEl(item, "decision")
                        });

                return (
                    TryGetStringEl(parsed, "campaignType", "reactivation"),
                    TryGetStringEl(parsed, "tone", "friendly"),
                    TryGetStringElOrNull(parsed, "recommendedSendTime"),
                    explanation
                );
            }
            catch
            {
                return ("reactivation", "friendly", null, []);
            }
        }

        private WizardContentDto ParseContent(string raw)
        {
            try
            {
                var cleaned = CleanJson(raw);
                var parsed = JsonSerializer.Deserialize<JsonElement>(cleaned, _jsonOptions);

                return new WizardContentDto
                {
                    Subject = TryGetStringEl(parsed, "subject"),
                    Preheader = TryGetStringEl(parsed, "preheader"),
                    HeroHeadline = TryGetStringEl(parsed, "hero_headline"),
                    BodyPara1 = TryGetStringEl(parsed, "body_para_1"),
                    BodyPara2 = TryGetStringEl(parsed, "body_para_2"),
                    CtaText = TryGetStringEl(parsed, "cta_text", "Learn More"),
                    SmsText = TryGetStringEl(parsed, "sms_text")
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Content parse failed");
                return new WizardContentDto();
            }
        }

        private static string BuildStrategySystemPrompt() =>
            """
    You are a CRM campaign strategist.
    Receive audience profile → generate campaign strategy.
    Campaign types: reactivation, retention, conversion, winback, upsell
    Tones: urgent, friendly, premium, promotional
    Rules:
    - Lapsed/LongTermLapsed → winback or reactivation, urgent tone
    - AtRisk → retention, friendly tone
    - Active + Low value → conversion, promotional tone
    - Active + High value → upsell, premium tone
    Return ONLY valid JSON:
    {
      "campaignType": "...",
      "tone": "...",
      "recommendedSendTime": "Day HH:MM",
      "decisionFlow": {
        "engagementSignal": "specific fact about engagement from profile",
        "engagementConclusion": "what this means for campaign type",
        "valueSignal": "specific fact about customer value from profile",
        "valueConclusion": "what this means for investment level",
        "channelSignal": "specific fact about channel coverage from profile",
        "channelConclusion": "which channel this recommends and why",
        "finalDecision": "one sentence summary of the strategy chosen"
      },
      "explanation": [
        {"signal": "...", "implication": "...", "decision": "..."}
      ]
    }
    Provide exactly 3 explanation points. Reference actual numbers.
    """;

        private static string BuildStrategyUserPrompt(
            SegmentProfileDto profile,
            GenerateStrategyRequestDto request) =>
            $"""
            AUDIENCE: {profile.BehaviourSummary}
            Engagement: {profile.EngagementLevel} | Value: {profile.ValueTier}
            Email coverage: {profile.EmailCoveragePercent}%
            Best visit day: {profile.RecommendedSendDay ?? "unknown"}
            Channel: {request.Channel} | Objective: {request.Objective}
            Generate strategy.
            """;
        // ════════════════════════════════════════════════════════════════════
        // MODEL ROUTING HELPERS
        // Reads model names from appsettings via _aiService properties
        // No hardcoded model strings anywhere in this class
        // ════════════════════════════════════════════════════════════════════

        // Fast model — extraction, question generation, selection matching
        // Short structured output — quality difference vs power model negligible
        private Task<(string Response, int Tokens)> CallFastAsync(
            string system, string user, int maxTokens = 150)
        {
            return _aiService.CallPublicAsync(
                system, user, maxTokens,
                model: _aiService.FastModel);
        }

        // Power model — strategy generation, content generation
        // Complex creative and reasoning tasks
        private Task<(string Response, int Tokens)> CallPowerAsync(
            string system, string user, int maxTokens = 600)
        {
            return _aiService.CallPublicAsync(
                system, user, maxTokens,
                model: _aiService.PowerModel);
        }

        // ── JSON helpers ──────────────────────────────────────────────────

        private static string? TryGetString(string json, string prop)
        {
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(json);
                return el.TryGetProperty(prop, out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }

        private static string TryGetStringEl(
            JsonElement el, string prop, string fallback = "")
        {
            try
            {
                return el.TryGetProperty(prop, out var v)
                    ? v.GetString() ?? fallback
                    : fallback;
            }
            catch { return fallback; }
        }

        private static string? TryGetStringElOrNull(JsonElement el, string prop)
        {
            try
            {
                return el.TryGetProperty(prop, out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }

        private static string CleanJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "{}";
            var s = raw.Trim();
            if (s.StartsWith("```json")) s = s[7..];
            else if (s.StartsWith("```")) s = s[3..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();
            var start = s.IndexOf('{');
            var end = s.LastIndexOf('}');
            return start >= 0 && end > start ? s[start..(end + 1)] : s;
        }

        private static string BuildFallbackQuestion(string field) => field switch
        {
            "channel" => "Will this be an Email campaign or SMS?",
            "objective" => "What is the main goal of this campaign?",
            "name" => "What would you like to call this campaign?",
            _ => "Could you tell me more?"
        };
    }
}