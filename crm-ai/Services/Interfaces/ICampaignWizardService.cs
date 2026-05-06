using crm_ai.DTOs;

namespace crm_ai.Services.Interfaces
{
    public interface ICampaignWizardService
    {
        Task<WizardResponseDto> TurnAsync(WizardRequestDto request);
    }
}