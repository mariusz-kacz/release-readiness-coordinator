using System.Globalization;
using System.Text;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Workflow;

internal sealed record DecisionSnapshotBuildResult(
    DecisionSnapshot Snapshot,
    HumanDecisionRequest Request);

internal sealed class DecisionSnapshotBuilder(
    ReleaseId releaseId,
    IApplicationDataService dataService,
    TimeProvider timeProvider)
{
    private readonly ReleaseId _releaseId =
        releaseId ?? throw new ArgumentNullException(nameof(releaseId));
    private readonly IApplicationDataService _dataService =
        dataService ?? throw new ArgumentNullException(nameof(dataService));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<DecisionSnapshotBuildResult> BuildAsync(
        EvaluationRound round,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(round);
        var detail = await _dataService.GetReleaseDetailAsync(_releaseId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release '{_releaseId}' does not exist.");
        EnsureLatestPassingRound(round, detail);

        var existingSnapshot = detail.DecisionSnapshots.SingleOrDefault(
            snapshot => snapshot.EvaluationRoundId == round.Id);
        if (existingSnapshot is not null)
        {
            var existingRequest = detail.HumanDecisionRequests.Single(
                request => request.SnapshotId == existingSnapshot.Id);
            return new DecisionSnapshotBuildResult(existingSnapshot, existingRequest);
        }

        var createdAt = new UtcInstant(_timeProvider.GetUtcNow());
        var snapshot = new DecisionSnapshot(
            Guid.NewGuid(),
            _releaseId,
            round.Id,
            round.RoundNumber,
            round.Results,
            round.Results.Min(result => result.ValidUntil!.Value),
            BuildBrief(round),
            createdAt);
        var request = new HumanDecisionRequest(
            Guid.NewGuid(),
            _releaseId,
            snapshot.Id,
            createdAt);
        var nextSequence = detail.Timeline.IsEmpty ? 1 : detail.Timeline[^1].Sequence + 1;
        var persistedRequest = await _dataService.OpenHumanDecisionRequestAsync(
            snapshot,
            request,
            new TimelineEntry(
                Guid.NewGuid(),
                _releaseId,
                nextSequence,
                TimelineEntryKind.ApprovalRequested,
                $"Round {round.RoundNumber} passed all checks; human approval requested.",
                createdAt),
            RoundAggregator.OperationKey(
                _releaseId,
                $"round:{round.RoundNumber}:approval-request"),
            cancellationToken);

        return new DecisionSnapshotBuildResult(snapshot, persistedRequest);
    }

    internal static string BuildBrief(EvaluationRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Results.Any(result => result.Outcome is not BranchOutcome.Passed))
        {
            throw new InvalidOperationException("A decision brief requires a fully passing round.");
        }

        var earliest = round.Results.Min(result => result.ValidUntil!.Value);
        var brief = new StringBuilder();
        brief.Append("Release ").Append(round.ReleaseId.Value)
            .Append(" passed all deterministic readiness checks in round ")
            .Append(round.RoundNumber.ToString(CultureInfo.InvariantCulture)).Append(".\n")
            .Append("Earliest validity bound: ").Append(earliest.Value.ToString("O", CultureInfo.InvariantCulture)).Append(".\n");

        foreach (var result in round.Results.OrderBy(result => result.Check))
        {
            brief.Append(result.Check).Append(": Passed; result ").Append(result.Id.ToString("D"))
                .Append("; evidence ").Append(result.EvidenceId!.Value.ToString("D"))
                .Append("; valid until ").Append(result.ValidUntil!.Value.Value.ToString("O", CultureInfo.InvariantCulture))
                .Append(".\n");
            foreach (var finding in result.Findings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                brief.Append("  ").Append(finding.Key).Append(": ").Append(finding.Value).Append("\n");
            }
        }

        return brief.ToString().TrimEnd('\n');
    }

    private void EnsureLatestPassingRound(EvaluationRound round, ReleaseDetailProjection detail)
    {
        var latestRound = detail.EvaluationRounds.LastOrDefault();
        if (round.ReleaseId != _releaseId
            || latestRound?.Id != round.Id
            || round.Results.Any(result => result.Outcome is not BranchOutcome.Passed))
        {
            throw new InvalidOperationException(
                "A decision snapshot requires the latest fully passing round for this release.");
        }
    }
}
