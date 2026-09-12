internal interface IClassifierRepository
{
    Task<ClassifierTree> GetTreeAsync(CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ClassifierCriterion>> CreateCriterionAsync(ClassifierCriterionCreate request, CancellationToken cancellationToken);
    Task<ChecklistOperationResult<ClassifierCriterion>> UpdateCriterionAsync(Guid id, ClassifierCriterionWrite request, CancellationToken cancellationToken);
    Task<bool> DeleteCriterionAsync(Guid id, CancellationToken cancellationToken);
}
