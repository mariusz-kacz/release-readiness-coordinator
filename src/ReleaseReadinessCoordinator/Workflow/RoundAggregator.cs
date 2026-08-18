using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using DomainBranchResult = ReleaseReadinessCoordinator.Domain.BranchResult;
using DomainRemediationRequest = ReleaseReadinessCoordinator.Domain.RemediationRequest;

namespace ReleaseReadinessCoordinator.Workflow;

internal sealed record RoundAggregation(
    EvaluationRound Round,
    DomainRemediationRequest? RemediationRequest);

internal sealed class RoundAggregator(
    IApplicationDataService dataService,
    TimeProvider timeProvider)
{
    private readonly IApplicationDataService _dataService =
        dataService ?? throw new ArgumentNullException(nameof(dataService));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<RoundAggregation> CompleteAsync(
        EvaluationRoundStart start,
        IReadOnlyCollection<DomainBranchResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(results);
        EnsureExplanationsAreComplete(results);

        var releaseRevision = results.FirstOrDefault()?.ReleaseRevision
            ?? throw new InvalidOperationException("A round cannot be completed without branch results.");
        var detail = await _dataService.GetReleaseDetailAsync(releaseRevision, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release revision '{releaseRevision.ReleaseId}/{releaseRevision.Revision}' does not exist.");
        var completedAt = new UtcInstant(_timeProvider.GetUtcNow());
        var round = new EvaluationRound(
            start.RoundId,
            releaseRevision,
            start.RoundNumber,
            start.StartedAt,
            completedAt,
            results);
        var nextSequence = detail.Timeline.IsEmpty
            ? 1
            : detail.Timeline[^1].Sequence + 1;
        var persistedRound = await _dataService.SaveEvaluationRoundAsync(
            round,
            new TimelineEntry(
                Guid.NewGuid(),
                releaseRevision,
                nextSequence,
                TimelineEntryKind.EvaluationCompleted,
                ExplainRound(round),
                completedAt),
            OperationKey(releaseRevision, $"round:{start.RoundNumber}"),
            cancellationToken);

        var problems = persistedRound.Results
            .Where(result => result.Outcome is not BranchOutcome.Passed)
            .ToArray();
        if (problems.Length == 0)
        {
            return new RoundAggregation(persistedRound, RemediationRequest: null);
        }

        var request = new DomainRemediationRequest(
            Guid.NewGuid(),
            releaseRevision,
            start.RoundNumber,
            completedAt,
            problems);
        var persistedRequest = await _dataService.OpenRemediationRequestAsync(
            request,
            new TimelineEntry(
                Guid.NewGuid(),
                releaseRevision,
                nextSequence + 1,
                TimelineEntryKind.RemediationRequested,
                $"Round {start.RoundNumber} requires remediation for {string.Join(", ", problems.Select(result => result.Check))}.",
                completedAt),
            OperationKey(releaseRevision, $"round:{start.RoundNumber}:remediation-request"),
            cancellationToken);
        return new RoundAggregation(persistedRound, persistedRequest);
    }

    private static void EnsureExplanationsAreComplete(IEnumerable<DomainBranchResult> results)
    {
        foreach (var result in results)
        {
            var requiredPrefix = result.Disposition switch
            {
                ExecutionDisposition.Executed => "Executed because",
                ExecutionDisposition.Reused => $"Reused from round {result.ReuseSourceRound} because",
                _ => throw new InvalidOperationException(
                    $"Unknown execution disposition '{result.Disposition}'."),
            };
            if (!result.PlanningDetail.StartsWith(requiredPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The {result.Check} result does not contain a complete {result.Disposition} explanation.");
            }
        }
    }

    private static string ExplainRound(EvaluationRound round) =>
        $"Round {round.RoundNumber} completed. "
        + string.Join(
            " ",
            round.Results.Select(result => $"{result.Check}: {result.PlanningDetail}"));

    internal static string OperationKey(ReleaseRevisionKey releaseRevision, string operation) =>
        $"workflow:{releaseRevision.ReleaseId}:{releaseRevision.Revision}:{operation}";
}
