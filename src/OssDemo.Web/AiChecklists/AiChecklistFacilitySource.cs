internal interface IAiChecklistFacilitySource
{
    Task<FacilityProfile?> GetProfileAsync(string slug, CancellationToken cancellationToken);
    Task<OperationalFacility?> GetFacilityAsync(string slug, CancellationToken cancellationToken);
}

internal sealed class AiChecklistFacilitySource(
    FacilityProfileService facilityProfiles,
    OperationalDataService operationalData) : IAiChecklistFacilitySource
{
    public Task<FacilityProfile?> GetProfileAsync(string slug, CancellationToken cancellationToken) =>
        facilityProfiles.GetAsync(slug, cancellationToken);

    public async Task<OperationalFacility?> GetFacilityAsync(string slug, CancellationToken cancellationToken) =>
        (await operationalData.GetFacilitiesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
