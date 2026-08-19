using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal interface IChangeEvidenceProvider : IEvidenceProvider<ChangeEvidenceRecord>;

internal sealed class SimulatedChangeEvidenceProvider : IChangeEvidenceProvider
{
    private readonly ChangeEvidenceRecord? _currentEvidence;
    private readonly int _knownTransientFailuresBeforeSuccess;
    private int _attempts;

    public SimulatedChangeEvidenceProvider(
        ChangeEvidenceRecord? currentEvidence,
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

    public ValueTask<ChangeEvidenceRecord?> GetCurrentAsync(
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
                "The simulated Change evidence source is temporarily unavailable.");
        }

        return ValueTask.FromResult(_currentEvidence);
    }
}
