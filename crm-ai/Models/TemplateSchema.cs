namespace crm_ai.Models
{
    /// <summary>
    /// Rich template schema loaded from templates.json.
    /// Each slot has type, validation rules, and a label.
    /// This is what separates a rendering engine from basic string replace.
    /// </summary>
    public class TemplateSchema
    {
        public string Id { get; set; } = "";
        public int Version { get; set; } = 1;
        public string Type { get; set; } = "email";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string PreviewImageUrl { get; set; } = "";
        public string[] SupportedObjectives { get; set; } = [];
        public string[] SupportedTones { get; set; } = [];
        public string[] SupportedChannels { get; set; } = [];

        // Key: slot name, Value: slot definition
        public Dictionary<string, SlotDefinition> Slots { get; set; } = [];
    }

    public class SlotDefinition
    {
        public string Type { get; set; } = "text";
        // "text" | "url" | "personalization"

        public bool Required { get; set; }
        public int MaxLength { get; set; } = 500;
        public string Label { get; set; } = "";

        // Default value used when slot is missing and not required
        public string? DefaultValue { get; set; }
    }

    /// <summary>
    /// Result of rendering a template.
    /// Contains the final HTML plus any validation issues found.
    /// </summary>
    public class RenderResult
    {
        public bool Success { get; set; }
        public string Html { get; set; } = "";
        public List<RenderValidationIssue> Issues { get; set; } = [];
        public Dictionary<string, string> AppliedSlots { get; set; } = [];
        // Slots after truncation/fallback applied — useful for audit
    }

    public class RenderValidationIssue
    {
        public string Severity { get; set; } = "";  // "error" | "warning"
        public string Slot { get; set; } = "";
        public string Message { get; set; } = "";
    }
}