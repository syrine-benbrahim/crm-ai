namespace crm_ai.Models
{
    public class TreeNode
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public TreeNode? Parent { get; set; }
        public List<TreeNode>? Children { get; set; }
        public string NodeCode { get; set; } = "";
        public string NodeName { get; set; } = "";
        public string? NodeDesc { get; set; }
        public string? EntityName { get; set; }
        public string? FieldName { get; set; }
        public string? DataType { get; set; }
        public int IsSelectable { get; set; }
 
        // ── NEW: AI optimisation columns ──────────────────────────────────
        // AiLabel: ultra-short label sent to AI in prompts (e.g. "Female",
        //   "Last 7 days", "London"). Max 50 chars. If null, NodeName is used.
        public string? AiLabel { get; set; }
 
        // SemanticCategory: stable, normalised category name used for catalog
        //   filtering (e.g. "Gender", "Age", "Recency", "Location", "Spend",
        //   "Loyalty"). If null, falls back to parent NodeName.
        public string? SemanticCategory { get; set; }
    }
}