namespace crm_ai.DTOs
{
    public class AiDescriptionRequestDto
    {
        /// <summary>
        /// The root group of the selection whose description we want to generate.
        /// Send the same RootGroup you'd use for preview/execute.
        /// </summary>
        public SelectionGroupDto RootGroup { get; set; } = null!;
    }
}