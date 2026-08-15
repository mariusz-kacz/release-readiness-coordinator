using System.Collections.Immutable;

namespace ReleaseReadinessCoordinator.Domain;

public readonly record struct UtcInstant : IComparable<UtcInstant>
{
    public UtcInstant(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A UTC instant must have a zero offset.", nameof(value));
        }

        Value = value;
    }

    public DateTimeOffset Value { get; }

    public int CompareTo(UtcInstant other) => Value.CompareTo(other.Value);

    public static bool operator <(UtcInstant left, UtcInstant right) => left.Value < right.Value;

    public static bool operator >(UtcInstant left, UtcInstant right) => left.Value > right.Value;
}

public sealed record UtcInterval
{
    public UtcInterval(UtcInstant start, UtcInstant end)
    {
        if (end < start)
        {
            throw new ArgumentException("An interval cannot end before it starts.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public UtcInstant Start { get; }

    public UtcInstant End { get; }
}

public sealed record ReleaseRevisionKey
{
    public ReleaseRevisionKey(string releaseId, int revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "A release revision must be positive.");
        }

        ReleaseId = DomainGuard.Required(releaseId, nameof(releaseId));
        Revision = revision;
    }

    public string ReleaseId { get; }

    public int Revision { get; }
}

public sealed record ReleaseSubmission
{
    public ReleaseSubmission(
        ReleaseRevisionKey key,
        string serviceName,
        string releaseVersion,
        UtcInterval requestedDeploymentWindow,
        IReadOnlyDictionary<string, string> dependencyRequirements,
        UtcInstant submittedAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(requestedDeploymentWindow);
        ArgumentNullException.ThrowIfNull(dependencyRequirements);

        Key = key;
        ServiceName = DomainGuard.Required(serviceName, nameof(serviceName));
        ReleaseVersion = DomainGuard.Required(releaseVersion, nameof(releaseVersion));
        RequestedDeploymentWindow = requestedDeploymentWindow;
        DependencyRequirements = dependencyRequirements.ToImmutableDictionary(
            pair => DomainGuard.Required(pair.Key, nameof(dependencyRequirements)),
            pair => DomainGuard.Required(pair.Value, nameof(dependencyRequirements)),
            StringComparer.Ordinal);
        SubmittedAt = submittedAt;
    }

    public ReleaseRevisionKey Key { get; }

    public string ServiceName { get; }

    public string ReleaseVersion { get; }

    public UtcInterval RequestedDeploymentWindow { get; }

    public ImmutableDictionary<string, string> DependencyRequirements { get; }

    public UtcInstant SubmittedAt { get; }
}

public enum ProcessPhase
{
    Evaluating = 1,
    WaitingForRemediation = 2,
    WaitingForApproval = 3,
    Approved = 4,
    Rejected = 5,
    Failed = 6,
}

public sealed record ReleaseRevision
{
    private ReleaseRevision(ReleaseSubmission submission, ProcessPhase phase, UtcInstant phaseChangedAt)
    {
        Submission = submission;
        Phase = phase;
        PhaseChangedAt = phaseChangedAt;
    }

    public ReleaseSubmission Submission { get; }

    public ProcessPhase Phase { get; }

    public UtcInstant PhaseChangedAt { get; }

    public bool IsTerminal => Phase is ProcessPhase.Approved or ProcessPhase.Rejected;

    public static ReleaseRevision Create(ReleaseSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return new ReleaseRevision(submission, ProcessPhase.Evaluating, submission.SubmittedAt);
    }

    public ReleaseRevision TransitionTo(ProcessPhase nextPhase, UtcInstant changedAt)
    {
        DomainGuard.Defined(nextPhase, nameof(nextPhase));
        if (changedAt < PhaseChangedAt)
        {
            throw new ArgumentException("A phase change cannot predate the current phase.", nameof(changedAt));
        }

        if (nextPhase == Phase)
        {
            return this;
        }

        if (IsTerminal)
        {
            throw new InvalidOperationException(
                $"Release revision '{Submission.Key.ReleaseId}/{Submission.Key.Revision}' is terminal and cannot be reopened.");
        }

        return new ReleaseRevision(Submission, nextPhase, changedAt);
    }
}

internal static class DomainGuard
{
    public static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }

    public static ImmutableArray<T> Copy<T>(IEnumerable<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return [.. values];
    }

    public static T Defined<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "An unknown enum value is not valid.");
        }

        return value;
    }

}
