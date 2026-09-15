internal interface IAiChecklistDraftStore
{
    Task<AiChecklistDraftState> CreateAsync(string facilitySlug, Guid? templateId, CancellationToken cancellationToken);
    Task<AiChecklistDraftState?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<AiChecklistDraftState>> UpdateAsync(Guid id, UpdateAiChecklistDraftRequest request, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<InspectionBasisDocument>> AddDocumentAsync(Guid draftId, string type, IFormFile file, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<bool>> DeleteDocumentAsync(Guid draftId, Guid documentId, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<AiChecklistDraftState>> AttachRunAsync(Guid draftId, Guid runId, CancellationToken cancellationToken);
}
