using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal sealed class ApplicationDataTestEvidenceProvider(IApplicationDataService dataService)
    : ITestEvidenceProvider
{
    private readonly IApplicationDataService _dataService =
        dataService ?? throw new ArgumentNullException(nameof(dataService));

    public async ValueTask<TestEvidenceRecord?> GetCurrentAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken) =>
        (TestEvidenceRecord?)await _dataService.GetCurrentEvidenceAsync(
            releaseId,
            EvidenceKind.Test,
            cancellationToken);
}

internal sealed class ApplicationDataSecurityEvidenceProvider(IApplicationDataService dataService)
    : ISecurityEvidenceProvider
{
    private readonly IApplicationDataService _dataService =
        dataService ?? throw new ArgumentNullException(nameof(dataService));

    public async ValueTask<SecurityEvidenceRecord?> GetCurrentAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken) =>
        (SecurityEvidenceRecord?)await _dataService.GetCurrentEvidenceAsync(
            releaseId,
            EvidenceKind.Security,
            cancellationToken);
}

internal sealed class ApplicationDataChangeEvidenceProvider(IApplicationDataService dataService)
    : IChangeEvidenceProvider
{
    private readonly IApplicationDataService _dataService =
        dataService ?? throw new ArgumentNullException(nameof(dataService));

    public async ValueTask<ChangeEvidenceRecord?> GetCurrentAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken) =>
        (ChangeEvidenceRecord?)await _dataService.GetCurrentEvidenceAsync(
            releaseId,
            EvidenceKind.Change,
            cancellationToken);
}
