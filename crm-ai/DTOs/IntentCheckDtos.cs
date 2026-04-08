using crm_ai.DTOs;

public class IntentCheckRequestDto
{
    public SelectionGroupDto RootGroup { get; set; } = new();
    public string Intent { get; set; } = string.Empty;
}

public class IntentCheckResponseDto
{
    public string Result { get; set; } = string.Empty;
    public string WhatItDoes { get; set; } = string.Empty;
    public string WhatYouWanted { get; set; } = string.Empty;
    public List<IntentGap> Gaps { get; set; } = new();
    public SelectionGroupDto? SuggestedFix { get; set; }
    public string? SuggestedFixDescription { get; set; }
    public int TokensUsed { get; set; }
}

public class IntentGap
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}