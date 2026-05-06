using crm_ai.DTOs;
using crm_ai.Models;
using crm_ai.Services.Interfaces;

namespace crm_ai.Services
{
    // ════════════════════════════════════════════════════════════════════════
    // TEMPLATE RECOMMENDATION SERVICE
    //
    // Recommends the best template given campaign objective + selection
    // description. Entirely C# — zero AI tokens, zero latency, deterministic.
    //
    // PIPELINE:
    //   Step 1 — Detect campaign type from combined text (objective + selection)
    //   Step 2 — Detect tone from campaign type + secondary signals
    //   Step 3 — Load templates from TemplateRenderingService (single source)
    //   Step 4 — Filter by channel
    //   Step 5 — Score each template: exact > type-only > channel-only
    //   Step 6 — Confidence check: below threshold → requiresManualBuild = true
    //   Step 7 — Build human-readable reason from the signals that fired
    //
    // WHY PURE C# AND NOT AI:
    //   Template recommendation is a classification task with a small, fixed
    //   output space. Signals are explicit keywords in a bounded vocabulary.
    //   There is no natural language variance that would justify an AI call
    //   here — compare with catalog filtering (AI call) which must handle
    //   "ladies" → Gender, "havent been in" → Recency. Template matching
    //   does not have that problem.
    //
    // WHY NOT USE THE STRATEGY AI CALL:
    //   CampaignWizardService runs a full AI strategy call (600 tokens,
    //   power model) before suggesting templates. That call builds a segment
    //   profile and reasons across engagement + value + channel coverage.
    //   For standalone template recommendation from a campaign record (without
    //   a wizard session), we cannot require that context. This service
    //   produces the same campaign type + tone signals from the raw text
    //   in ~0ms with 0 tokens. The wizard may still use the AI strategy for
    //   the richer profile, but template matching is always done here.
    // ════════════════════════════════════════════════════════════════════════

    public class TemplateRecommendationService : ITemplateRecommendationService
    {
        private readonly ITemplateRenderingService _templateEngine;
        private readonly ILogger<TemplateRecommendationService> _logger;

        // A match is considered "confident" when the top template scores
        // at least this many points. Below this → requiresManualBuild = true.
        private const int ConfidenceThreshold = 2;

        public TemplateRecommendationService(
            ITemplateRenderingService templateEngine,
            ILogger<TemplateRecommendationService> logger)
        {
            _templateEngine = templateEngine;
            _logger = logger;
        }

        // ── Main entry point ─────────────────────────────────────────────────

        public async Task<TemplateRecommendationResultDto> RecommendAsync(
            string objective,
            string channel,
            string? selectionDescription)
        {
            // Combine objective + selection description — both carry signal.
            // e.g. objective "email campaign" + description "lapsed customers"
            // → "lapsed" fires winback type even if objective is vague.
            var combined = $"{objective} {selectionDescription ?? ""}".ToLower();

            var (campaignType, typeReason) = DetectCampaignType(combined);
            var (tone, toneReason) = DetectTone(combined, campaignType);

            _logger.LogInformation(
                "Template recommendation — type={Type} ({TypeReason}), " +
                "tone={Tone} ({ToneReason}), channel={Channel}",
                campaignType, typeReason, tone, toneReason, channel);

            // Load from TemplateRenderingService — single source of truth (templates.json)
            // Never from the hardcoded GetBuiltInTemplates() list in the wizard
            var allTemplates = await _templateEngine.GetAllTemplatesAsync();

            var channelTemplates = allTemplates
                .Where(t => t.SupportedChannels
                    .Contains(channel, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // No templates for this channel at all → manual build required
            if (channelTemplates.Count == 0)
            {
                _logger.LogWarning(
                    "No templates found for channel {Channel}", channel);

                return new TemplateRecommendationResultDto
                {
                    RecommendedTemplateId = null,
                    CampaignType = campaignType,
                    Tone = tone,
                    RecommendationReason = null,
                    Templates = [],
                    RequiresManualBuild = true,
                    ManualBuildReason =
                        $"No {channel} templates are available. " +
                        $"You can build your content manually below."
                };
            }

            // Score every template
            var scored = channelTemplates
                .Select(t => new
                {
                    Template = t,
                    Score = ScoreTemplate(t, campaignType, tone)
                })
                .OrderByDescending(x => x.Score)
                .ToList();

            var top = scored.First();
            bool isConfident = top.Score >= ConfidenceThreshold;

            string? recommendedId = isConfident ? top.Template.Id : null;
            string reason = BuildReason(campaignType, tone, typeReason, toneReason,
                top.Template, isConfident);

            _logger.LogInformation(
                "Template result: recommended={Id}, score={Score}, confident={C}, reason={R}",
                recommendedId, top.Score, isConfident, reason);

            var suggestions = scored.Select(x =>
            {
                bool isRec = isConfident && x.Template.Id == recommendedId;
                return new TemplateSuggestionDto
                {
                    Id = x.Template.Id,
                    Name = x.Template.Name,
                    Description = x.Template.Description,
                    PreviewImageUrl = x.Template.PreviewImageUrl,
                    SupportedObjectives = x.Template.SupportedObjectives,
                    SupportedTones = x.Template.SupportedTones,
                    ContentSlots = x.Template.Slots.Keys.ToArray(),
                    IsRecommended = isRec,
                    RecommendationReason = isRec ? reason : null
                };
            }).ToList();

            return new TemplateRecommendationResultDto
            {
                RecommendedTemplateId = recommendedId,
                CampaignType = campaignType,
                Tone = tone,
                RecommendationReason = isConfident ? reason : null,
                Templates = suggestions,
                RequiresManualBuild = false,
                ManualBuildReason = null
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // TEMPLATE SCORING
        //
        // Returns a score integer — higher is better.
        //   Exact match (type AND tone):  3
        //   Type match only:              2
        //   Channel match only:           1
        //   No match:                     0
        //
        // Why integers not booleans: scoring allows future extension
        // (e.g. bonus points for matching description keywords against
        // template description) without changing the interface.
        // ════════════════════════════════════════════════════════════════════

        private static int ScoreTemplate(
            TemplateSchema template,
            string campaignType,
            string tone)
        {
            bool typeMatch = template.SupportedObjectives
                .Contains(campaignType, StringComparer.OrdinalIgnoreCase);
            bool toneMatch = template.SupportedTones
                .Contains(tone, StringComparer.OrdinalIgnoreCase);

            if (typeMatch && toneMatch) return 3;  // exact
            if (typeMatch) return 2;               // type only
            return 1;                              // channel only (already filtered)
        }

        // ════════════════════════════════════════════════════════════════════
        // CAMPAIGN TYPE DETECTION
        //
        // Evaluated in priority order — first match wins.
        // Why priority order matters:
        //   "win back loyal customers" has both "win back" (winback) and
        //   "loyal" (retention). Winback wins because re-engagement is the
        //   dominant intent — not rewarding loyalty.
        // ════════════════════════════════════════════════════════════════════

        private static readonly (string[] Triggers, string Type, string Reason)[] TypeSignals =
        [
            (
                ["lapsed", "win back", "winback", "win-back", "inactive",
                 "haven't visited", "havent visited", "not visited",
                 "re-engage", "reengage", "reactivate", "bring back",
                 "lost customer", "long-term lapsed", "long term lapsed"],
                "winback",
                "lapsed/inactive audience detected"
            ),
            (
                ["at risk", "at-risk", "churn", "losing", "slipping",
                 "about to lapse", "reduce churn"],
                "retention",
                "at-risk audience detected"
            ),
            (
                ["new customer", "first visit", "first time", "never visited",
                 "acquisition", "sign up", "onboard"],
                "conversion",
                "new/conversion audience detected"
            ),
            (
                ["upsell", "upgrade", "vip", "exclusive",
                 "high value", "high spend", "high spender", "top customer"],
                "upsell",
                "high-value/upsell audience detected"
            ),
            (
                ["loyal", "loyalty", "frequent", "regular",
                 "reward", "thank", "appreciate", "engage"],
                "retention",
                "loyal/engaged audience detected"
            ),
            (
                ["sale", "offer", "discount", "promotion", "deal",
                 "limited time", "flash", "savings"],
                "conversion",
                "promotional objective detected"
            ),
        ];

        private static (string Type, string Reason) DetectCampaignType(string text)
        {
            foreach (var (triggers, type, reason) in TypeSignals)
                if (triggers.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    return (type, reason);

            return ("reactivation", "no specific signal — defaulting to reactivation");
        }

        // ════════════════════════════════════════════════════════════════════
        // TONE DETECTION
        //
        // Decision table:
        //   winback / reactivation  → urgent    (lapsed need a strong reason)
        //   upsell                  → premium   (high-value expect exclusivity)
        //   retention + high spend  → premium   (loyal high-value → exclusive)
        //   retention               → friendly  (engaged → warm, not pushy)
        //   conversion              → promotional
        // ════════════════════════════════════════════════════════════════════

        private static readonly string[] HighSpendSignals =
            ["£70", "£80", "£90", "£100", "£150", "£200", "£400", "£600",
             "high spend", "high value", "high spender", "premium", "vip",
             "over £50", "over £70", "top customer", "best customer"];

        private static (string Tone, string Reason) DetectTone(
            string text, string campaignType)
        {
            bool hasHighSpend = HighSpendSignals.Any(s =>
                text.Contains(s, StringComparison.OrdinalIgnoreCase));

            return campaignType switch
            {
                "winback" or "reactivation" =>
                    ("urgent", "lapsed customers need urgency to re-engage"),
                "upsell" =>
                    ("premium", "high-value audience expects a premium tone"),
                "retention" when hasHighSpend =>
                    ("premium", "high-value loyal audience — premium tone"),
                "retention" =>
                    ("friendly", "engaged audience responds better to warmth"),
                "conversion" =>
                    ("promotional", "conversion objective needs a promotional push"),
                _ => ("friendly", "default tone")
            };
        }

        // ── Human-readable reason — pure C#, never AI ─────────────────────

        private static string BuildReason(
            string campaignType,
            string tone,
            string typeReason,
            string toneReason,
            TemplateSchema template,
            bool isConfident)
        {
            if (!isConfident)
                return $"Closest available template — " +
                       $"consider building content manually for a better fit";

            var r = $"{char.ToUpper(typeReason[0])}{typeReason[1..]}; {toneReason}";
            return r;
        }
    }
}