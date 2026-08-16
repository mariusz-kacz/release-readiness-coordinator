namespace ReleaseReadinessCoordinator.Domain;

public static class FreshnessDeadlines
{
    public static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromHours(24);

    public static UtcInstant Calculate(
        EvidenceRecord evidence,
        UtcInstant? earlierEvidenceBound = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var observedAt = evidence switch
        {
            TestEvidenceRecord test => test.CompletedAt,
            SecurityEvidenceRecord security => security.ScannedAt,
            _ => throw new ArgumentException(
                "Only Test and Security evidence use the 24-hour freshness policy.",
                nameof(evidence)),
        };
        if (!observedAt.HasValue)
        {
            throw new InvalidOperationException(
                $"{evidence.Kind} evidence has no observation timestamp from which to calculate freshness.");
        }

        var maximumDeadline = new UtcInstant(observedAt.Value.Value.Add(MaximumEvidenceAge));
        return earlierEvidenceBound.HasValue && earlierEvidenceBound.Value < maximumDeadline
            ? earlierEvidenceBound.Value
            : maximumDeadline;
    }

    public static bool IsCurrent(UtcInstant deadline, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return timeProvider.GetUtcNow().ToUniversalTime() < deadline.Value;
    }
}
