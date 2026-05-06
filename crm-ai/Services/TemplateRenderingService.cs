using crm_ai.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace crm_ai.Services
{
    /// <summary>
    /// The template rendering engine.
    ///
    /// Pipeline:
    ///   1. Load template schema from templates.json
    ///   2. Load HTML file from disk
    ///   3. Validate all slots against schema (required, maxLength, type)
    ///   4. Apply fallbacks for missing optional slots
    ///   5. Replace {{slot}} placeholders
    ///   6. Process {{#if slot}}...{{/if}} conditional blocks
    ///   7. Return RenderResult with HTML + validation issues
    ///
    /// Why a service and not static helpers:
    ///   - Schema is cached in IMemoryCache (loaded once, reused)
    ///   - IWebHostEnvironment for file paths
    ///   - ILogger for audit trail
    ///   - Testable via interface
    /// </summary>
    public class TemplateRenderingService : ITemplateRenderingService
    {
        private readonly IWebHostEnvironment _env;
        private readonly IMemoryCache _cache;
        private readonly ILogger<TemplateRenderingService> _logger;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        // Matches {{slot_name}}
        private static readonly Regex SlotRegex =
            new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

        // Matches {{#if slot_name}}...content...{{/if}}
        private static readonly Regex IfBlockRegex =
            new(@"\{\{#if\s+(\w+)\}\}(.*?)\{\{/if\}\}",
                RegexOptions.Compiled | RegexOptions.Singleline);

        public TemplateRenderingService(
            IWebHostEnvironment env,
            IMemoryCache cache,
            ILogger<TemplateRenderingService> logger)
        {
            _env = env;
            _cache = cache;
            _logger = logger;
        }

        // ── Public API ────────────────────────────────────────────────

        public async Task<List<TemplateSchema>> GetAllTemplatesAsync()
        {
            return await LoadCatalogAsync();
        }

        public async Task<TemplateSchema?> GetTemplateAsync(string id)
        {
            var all = await LoadCatalogAsync();
            return all.FirstOrDefault(t =>
                t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Recommend template deterministically from strategy output.
        /// No AI call — pure metadata matching.
        /// Exact match (objective + tone) → objective only → channel only → first.
        /// </summary>
        public async Task<TemplateSchema?> RecommendAsync(
            string campaignType, string tone, string channel)
        {
            var templates = await LoadCatalogAsync();
            var filtered = templates
                .Where(t => t.SupportedChannels
                    .Contains(channel, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // Exact match
            var exact = filtered.FirstOrDefault(t =>
                t.SupportedObjectives.Contains(campaignType, StringComparer.OrdinalIgnoreCase) &&
                t.SupportedTones.Contains(tone, StringComparer.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // Objective only
            var byObj = filtered.FirstOrDefault(t =>
                t.SupportedObjectives.Contains(campaignType, StringComparer.OrdinalIgnoreCase));
            if (byObj != null) return byObj;

            return filtered.FirstOrDefault();
        }

        /// <summary>
        /// Core rendering method.
        /// Validates → applies fallbacks → replaces → processes conditionals.
        /// </summary>
        public async Task<RenderResult> RenderAsync(
            string templateId,
            Dictionary<string, string> slots)
        {
            var schema = await GetTemplateAsync(templateId);
            if (schema == null)
            {
                _logger.LogWarning(
                    "Template '{Id}' not found — using fallback", templateId);
                return new RenderResult
                {
                    Success = false,
                    Html = RenderFallback(slots),
                    Issues = [new() {
                        Severity = "error",
                        Slot = "template",
                        Message = $"Template '{templateId}' not found."
                    }]
                };
            }

            // ── Step 1: Validate ──────────────────────────────────────
            var issues = Validate(schema, slots);

            // ── Step 2: Apply fallbacks for missing optional slots ─────
            var resolvedSlots = ApplyFallbacks(schema, slots);

            // ── Step 3: Load HTML ──────────────────────────────────────
            var htmlPath = Path.Combine(_env.WebRootPath, schema.FilePath);
            if (!File.Exists(htmlPath))
            {
                _logger.LogWarning("Template file missing: {Path}", htmlPath);
                return new RenderResult
                {
                    Success = false,
                    Html = RenderFallback(resolvedSlots),
                    Issues = [.. issues, new() {
                        Severity = "error",
                        Slot = "template",
                        Message = $"Template HTML file not found at {schema.FilePath}."
                    }]
                };
            }

            var html = await File.ReadAllTextAsync(htmlPath);

            // ── Step 4: Process {{#if slot}}...{{/if}} blocks ─────────
            html = ProcessConditionalBlocks(html, resolvedSlots);

            // ── Step 5: Replace all {{slot}} placeholders ─────────────
            html = ReplaceSlots(html, resolvedSlots);

            // ── Step 6: Remove any remaining unfilled placeholders ─────
            html = SlotRegex.Replace(html, "");

            _logger.LogInformation(
                "Template '{Id}' v{Version} rendered — {Len} chars, {IssueCount} issues",
                schema.Id, schema.Version, html.Length, issues.Count);

            return new RenderResult
            {
                Success = !issues.Any(i => i.Severity == "error"),
                Html = html,
                Issues = issues,
                AppliedSlots = resolvedSlots
            };
        }

        // ── Validation ────────────────────────────────────────────────

        /// <summary>
        /// Validates slot values against schema rules.
        /// Returns list of issues — errors block rendering, warnings are advisory.
        /// </summary>
        private static List<RenderValidationIssue> Validate(
            TemplateSchema schema,
            Dictionary<string, string> slots)
        {
            var issues = new List<RenderValidationIssue>();

            foreach (var (slotName, def) in schema.Slots)
            {
                slots.TryGetValue(slotName, out var value);
                var isEmpty = string.IsNullOrWhiteSpace(value);

                // Required check
                if (def.Required && isEmpty && def.DefaultValue == null)
                {
                    issues.Add(new()
                    {
                        Severity = "error",
                        Slot = slotName,
                        Message = $"'{def.Label}' is required but missing."
                    });
                    continue;
                }

                if (isEmpty) continue;

                // Length check
                if (value!.Length > def.MaxLength)
                {
                    issues.Add(new()
                    {
                        Severity = "warning",
                        Slot = slotName,
                        Message = $"'{def.Label}' is {value.Length} chars " +
                                  $"(max {def.MaxLength}). Will be truncated."
                    });
                }

                // URL format check
                if (def.Type == "url" && !isEmpty &&
                    !value.StartsWith("http") && value != "#")
                {
                    issues.Add(new()
                    {
                        Severity = "warning",
                        Slot = slotName,
                        Message = $"'{def.Label}' does not appear to be a valid URL."
                    });
                }
            }

            return issues;
        }

        // ── Fallback application ──────────────────────────────────────

        private static Dictionary<string, string> ApplyFallbacks(
            TemplateSchema schema,
            Dictionary<string, string> slots)
        {
            var resolved = new Dictionary<string, string>(
                slots, StringComparer.OrdinalIgnoreCase);

            foreach (var (slotName, def) in schema.Slots)
            {
                resolved.TryGetValue(slotName, out var value);

                if (string.IsNullOrWhiteSpace(value))
                {
                    // Use schema default if provided
                    if (def.DefaultValue != null)
                        resolved[slotName] = def.DefaultValue;
                }
                else
                {
                    // Truncate if over limit (warn was already issued)
                    if (value.Length > def.MaxLength)
                        resolved[slotName] = value[..def.MaxLength].TrimEnd() + "…";
                }
            }

            // Always ensure URLs have a fallback
            if (!resolved.ContainsKey("cta_url") ||
                string.IsNullOrWhiteSpace(resolved["cta_url"]))
                resolved["cta_url"] = "#";

            if (!resolved.ContainsKey("unsubscribe_url") ||
                string.IsNullOrWhiteSpace(resolved["unsubscribe_url"]))
                resolved["unsubscribe_url"] = "#";

            return resolved;
        }

        // ── Rendering ─────────────────────────────────────────────────

        /// <summary>
        /// Processes {{#if slot}}...content...{{/if}} blocks.
        /// If the slot has a value — keep the content.
        /// If the slot is empty — remove the entire block.
        /// </summary>
        private static string ProcessConditionalBlocks(
            string html,
            Dictionary<string, string> slots)
        {
            return IfBlockRegex.Replace(html, match =>
            {
                var slotName = match.Groups[1].Value;
                var content = match.Groups[2].Value;

                slots.TryGetValue(slotName, out var value);
                return string.IsNullOrWhiteSpace(value) ? "" : content;
            });
        }

        private static string ReplaceSlots(
            string html,
            Dictionary<string, string> slots)
        {
            return SlotRegex.Replace(html, match =>
            {
                var slotName = match.Groups[1].Value;
                slots.TryGetValue(slotName, out var value);
                return System.Web.HttpUtility.HtmlEncode(value ?? "");
            });
        }

        // ── Schema loading ────────────────────────────────────────────

        private async Task<List<TemplateSchema>> LoadCatalogAsync()
        {
            const string cacheKey = "template_schema_catalog";
            if (_cache.TryGetValue(cacheKey, out List<TemplateSchema>? cached)
                && cached != null)
                return cached;

            var path = Path.Combine(
                _env.WebRootPath, "templates", "templates.json");

            if (!File.Exists(path))
            {
                _logger.LogWarning(
                    "templates.json not found at {Path}", path);
                return [];
            }

            var json = await File.ReadAllTextAsync(path);
            var schemas = JsonSerializer.Deserialize<List<TemplateSchema>>(
                json, _jsonOptions) ?? [];

            _cache.Set(cacheKey, schemas, TimeSpan.FromHours(4));
            _logger.LogInformation(
                "Template catalog loaded: {Count} templates", schemas.Count);

            return schemas;
        }

        // ── Fallback HTML ─────────────────────────────────────────────

        private static string RenderFallback(Dictionary<string, string> slots)
        {
            slots.TryGetValue("hero_headline", out var headline);
            slots.TryGetValue("body_para_1", out var body1);
            slots.TryGetValue("body_para_2", out var body2);
            slots.TryGetValue("cta_text", out var cta);
            slots.TryGetValue("subject", out var subject);

            return $"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="UTF-8"><title>{Encode(subject ?? "")}</title></head>
            <body style="font-family:Arial,sans-serif;max-width:600px;margin:32px auto;padding:0 24px;">
              <h1 style="color:#1a1a1a;font-size:28px;line-height:1.2;">
                {Encode(headline ?? "")}
              </h1>
              <p style="color:#374151;font-size:16px;line-height:1.7;">
                {Encode(body1 ?? "")}
              </p>
              {(string.IsNullOrWhiteSpace(body2) ? "" : $"<p style=\"color:#374151;font-size:16px;line-height:1.7;\">{Encode(body2)}</p>")}
              <a href="#" style="display:inline-block;background:#2563eb;color:#fff;
                 font-size:16px;font-weight:700;text-decoration:none;
                 padding:14px 28px;border-radius:6px;margin-top:24px;">
                {Encode(cta ?? "Learn More")}
              </a>
            </body>
            </html>
            """;
        }

        private static string Encode(string s) =>
            System.Web.HttpUtility.HtmlEncode(s);
    }
}