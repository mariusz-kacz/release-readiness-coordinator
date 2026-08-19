using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal interface ITestEvidenceProvider : IEvidenceProvider<TestEvidenceRecord>;

internal sealed class SimulatedTestEvidenceProvider : ITestEvidenceProvider
{
    private readonly TestEvidenceRecord? _currentEvidence;
    private readonly int _knownTransientFailuresBeforeSuccess;
    private int _attempts;

    public SimulatedTestEvidenceProvider(
        TestEvidenceRecord? currentEvidence,
        int knownTransientFailuresBeforeSuccess = 0)
    {
        if (knownTransientFailuresBeforeSuccess < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(knownTransientFailuresBeforeSuccess));
        }

        if (currentEvidence is null && knownTransientFailuresBeforeSuccess > 0)
        {
            throw new ArgumentException(
                "A simulated transient failure must identify a current evidence record.",
                nameof(currentEvidence));
        }

        _currentEvidence = currentEvidence;
        _knownTransientFailuresBeforeSuccess = knownTransientFailuresBeforeSuccess;
    }

    public ValueTask<TestEvidenceRecord?> GetCurrentAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        cancellationToken.ThrowIfCancellationRequested();
        _attempts++;

        if (_attempts <= _knownTransientFailuresBeforeSuccess)
        {
            throw new KnownTransientEvidenceProviderException(
                _currentEvidence!.Id,
                "The simulated Test evidence source is temporarily unavailable.");
        }

        return ValueTask.FromResult(_currentEvidence);
    }
}
