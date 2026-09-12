internal interface IChecklistRepository
{
    Task<IReadOnlyList<ChecklistTemplateSummary>> ListTemplatesAsync(CancellationToken cancellationToken);
    Task<ChecklistTemplateDetails?> GetTemplateAsync(Guid id, CancellationToken cancellationToken);
    Task<ChecklistTemplateDetails> CreateTemplateAsync(ChecklistTemplateWriteRequest request, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ChecklistTemplateDetails>> UpdateTemplateAsync(Guid id, ChecklistTemplateWriteRequest request, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ChecklistTemplateDetails>> CopyTemplateAsync(Guid id, string name, CancellationToken cancellationToken);
    Task<bool> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ChecklistDetails>> CreateDraftAsync(CreateChecklistRequest request, CancellationToken cancellationToken);
    Task<ChecklistDetails?> GetChecklistAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChecklistSummary>> ListDraftsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ChecklistSummary>> ListHistoryAsync(ChecklistHistoryFilter filter, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ChecklistDetails>> AddDraftItemAsync(Guid id, AddChecklistItemRequest request, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ChecklistDetails>> UpdateDraftItemAsync(Guid id, Guid itemId, UpdateChecklistItemRequest request, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ChecklistDetails>> ApproveAsync(Guid id, string approvedBy, CancellationToken cancellationToken);
}
