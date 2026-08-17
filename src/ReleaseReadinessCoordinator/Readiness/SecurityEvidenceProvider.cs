using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal interface ISecurityEvidenceProvider : IEvidenceProvider<SecurityEvidenceRecord>;

internal sealed class SimulatedSecurityEvidenceProvider : ISecurityEvidenceProvider
{
    private readonly SecurityEvidenceRecord? _currentEvidence;
    private readonly int _knownTransientFailuresBeforeSuccess;
    private int _attempts;

    public SimulatedSecurityEvidenceProvider(
        SecurityEvidenceRecord? currentEvidence,
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

    public ValueTask<SecurityEvidenceRecord?> GetCurrentAsync(
        ReleaseRevisionKey releaseRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(releaseRevision);
        cancellationToken.ThrowIfCancellationRequested();
        _attempts++;

        if (_attempts <= _knownTransientFailuresBeforeSuccess)
        {
            throw new KnownTransientEvidenceProviderException(
                _currentEvidence!.Id,
                "The simulated Security evidence source is temporarily unavailable.");
        }

        return ValueTask.FromResult(_currentEvidence);
    }
}
