using System.Collections.ObjectModel;
using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using DomainRemediationRequest = ReleaseReadinessCoordinator.Domain.RemediationRequest;

namespace ReleaseReadinessCoordinator.Workflow;

public static class ReleaseWorkflowExecutorIds
{
    public const string Planner = "readiness-planner";
    public const string Test = "test-readiness";
    public const string Security = "security-readiness";
    public const string Change = "change-readiness";
    public const string Aggregator = "readiness-aggregator";
    public const string RemediationHandler = "remediation-handler";
    public const string DecisionSnapshotBuilder = "decision-snapshot-builder";
    public const string HumanDecisionHandler = "human-decision-handler";
    public const string ApprovalCompletion = "approval-completion";

    public static IReadOnlyDictionary<ReadinessBranch, string> Branches { get; } =
        new ReadOnlyDictionary<ReadinessBranch, string>(
            new Dictionary<ReadinessBranch, string>
            {
                [ReadinessBranch.Test] = Test,
                [ReadinessBranch.Security] = Security,
                [ReadinessBranch.Change] = Change,
            });
}

public static class ReleaseWorkflowPortIds
{
    public const string Remediation = "remediation-request";
    public const string Approval = "approval-request";
}

internal sealed partial class ReadinessPlanner : Executor
{
    private readonly ReleaseRevisionKey _releaseRevision;
    private readonly IApplicationDataService _dataService;
    private readonly RoundPlanner _planner;

    public ReadinessPlanner(
        ReleaseRevisionKey releaseRevision,
        IApplicationDataService dataService,
        TimeProvider timeProvider)
        : base(ReleaseWorkflowExecutorIds.Planner)
    {
        _releaseRevision = releaseRevision ?? throw new ArgumentNullException(nameof(releaseRevision));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _planner = new RoundPlanner(timeProvider);
    }

    [MessageHandler(Send = [typeof(PlannedBranchWorkItem)])]
    private async ValueTask PlanAsync(
        EvaluationRoundStart start,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var detail = await _dataService.GetReleaseDetailAsync(_releaseRevision, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release revision '{_releaseRevision.ReleaseId}/{_releaseRevision.Revision}' does not exist.");
        var previousRound = detail.EvaluationRounds.LastOrDefault();
        if ((start.RoundNumber == 1 && previousRound is not null)
            || (start.RoundNumber > 1 && previousRound?.RoundNumber != start.RoundNumber - 1))
        {
            throw new InvalidOperationException(
                $"Round {start.RoundNumber} does not immediately follow durable evaluation history.");
        }

        var previousResults = previousRound?.Results ?? [];
        var currentEvidenceIds = detail.CurrentEvidence.ToDictionary(
            pair => CheckFor(pair.Key),
            pair => pair.Value.Id);
        var planned = _planner.Plan(new RoundPlanningRequest(
            _releaseRevision,
            start.RoundNumber,
            previousResults,
            currentEvidenceIds,
            start.ExplicitlySelectedChecks));

        foreach (var workItem in planned)
        {
            var source = workItem.ReuseSourceResultId.HasValue
                ? previousResults.Single(result => result.Id == workItem.ReuseSourceResultId.Value)
                : null;
            currentEvidenceIds.TryGetValue(workItem.Check, out var currentEvidenceId);
            await context.SendMessageAsync(
                new PlannedBranchWorkItem(
                    start,
                    workItem,
                    Guid.NewGuid(),
                    source,
                    currentEvidenceId == Guid.Empty ? null : currentEvidenceId),
                cancellationToken);
        }
    }

    private static ReadinessCheck CheckFor(EvidenceKind kind) => kind switch
    {
        EvidenceKind.Test => ReadinessCheck.Test,
        EvidenceKind.Security => ReadinessCheck.Security,
        EvidenceKind.Change => ReadinessCheck.Change,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

internal sealed partial class ReadinessBranchExecutor : Executor
{
    private readonly ReadinessBranch _branch;
    private readonly ReadinessCheck _check;
    private readonly ReleaseSubmission _submission;
    private readonly Func<
        ReleaseSubmission,
        Domain.BranchWorkItem,
        CancellationToken,
        Task<Domain.BranchResult>> _execute;
    private readonly ResultReuse _reuse;

    public ReadinessBranchExecutor(
        ReleaseSubmission submission,
        ITestEvidenceProvider provider,
        ITestReadinessPolicy policy,
        TimeProvider timeProvider)
        : base(ReleaseWorkflowExecutorIds.Test)
    {
        _branch = ReadinessBranch.Test;
        _check = ReadinessCheck.Test;
        _submission = submission;
        _execute = new TestReadinessBranchExecutor(provider, policy).ExecuteAsync;
        _reuse = new ResultReuse(timeProvider);
    }

    public ReadinessBranchExecutor(
        ReleaseSubmission submission,
        ISecurityEvidenceProvider provider,
        ISecurityReadinessPolicy policy,
        TimeProvider timeProvider)
        : base(ReleaseWorkflowExecutorIds.Security)
    {
        _branch = ReadinessBranch.Security;
        _check = ReadinessCheck.Security;
        _submission = submission;
        _execute = new SecurityReadinessBranchExecutor(provider, policy).ExecuteAsync;
        _reuse = new ResultReuse(timeProvider);
    }

    public ReadinessBranchExecutor(
        ReleaseSubmission submission,
        IChangeEvidenceProvider provider,
        IChangeReadinessPolicy policy,
        TimeProvider timeProvider)
        : base(ReleaseWorkflowExecutorIds.Change)
    {
        _branch = ReadinessBranch.Change;
        _check = ReadinessCheck.Change;
        _submission = submission;
        _execute = new ChangeReadinessBranchExecutor(provider, policy).ExecuteAsync;
        _reuse = new ResultReuse(timeProvider);
    }

    [MessageHandler]
    private async ValueTask<CompletedBranchWork> EvaluateAsync(
        PlannedBranchWorkItem work,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (work.WorkItem.Check != _check)
        {
            throw new InvalidOperationException(
                $"Executor '{Id}' cannot process {work.WorkItem.Check} work.");
        }

        var result = work.WorkItem.Disposition switch
        {
            WorkDisposition.Execute => await _execute(
                _submission,
                work.WorkItem,
                cancellationToken),
            WorkDisposition.Reuse => _reuse.Create(
                work.ResultId,
                work.WorkItem,
                work.ReuseSource
                    ?? throw new InvalidOperationException("Reuse work has no source result."),
                work.CurrentEvidenceId),
            _ => throw new InvalidOperationException(
                $"Unknown work disposition '{work.WorkItem.Disposition}'."),
        };

        return new CompletedBranchWork(work.Round, _branch, Id, result);
    }
}

internal sealed partial class ReadinessAggregator : Executor, IResettableExecutor
{
    private readonly List<CompletedBranchWork> _results = [];
    private readonly RoundAggregator _aggregator;

    public ReadinessAggregator(
        IApplicationDataService dataService,
        TimeProvider timeProvider)
        : base(ReleaseWorkflowExecutorIds.Aggregator)
    {
        _aggregator = new RoundAggregator(dataService, timeProvider);
    }

    [MessageHandler(Send = [typeof(DomainRemediationRequest), typeof(BuildDecisionSnapshot)])]
    private async ValueTask ReceiveAsync(
        CompletedBranchWork completed,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        Validate(completed);
        if (_results.Count > 0 && _results[0].Round.Id != completed.Round.Id)
        {
            throw new InvalidOperationException("Branch results belong to different evaluation rounds.");
        }

        if (_results.Any(item => item.Branch == completed.Branch))
        {
            throw new InvalidOperationException(
                $"Duplicate result for readiness branch '{completed.Branch}'.");
        }

        _results.Add(completed);
        if (_results.Count != ReleaseWorkflowExecutorIds.Branches.Count)
        {
            return;
        }

        var aggregation = await _aggregator.CompleteAsync(
            completed.Round,
            _results.Select(item => item.Result).ToArray(),
            cancellationToken);
        if (aggregation.RemediationRequest is not null)
        {
            await context.SendMessageAsync(aggregation.RemediationRequest, cancellationToken);
        }
        else
        {
            await context.SendMessageAsync(
                new BuildDecisionSnapshot(aggregation.Round),
                cancellationToken);
        }

        _results.Clear();
    }

    public ValueTask ResetAsync()
    {
        _results.Clear();
        return ValueTask.CompletedTask;
    }

    private static void Validate(CompletedBranchWork completed)
    {
        if (!ReleaseWorkflowExecutorIds.Branches.TryGetValue(completed.Branch, out var expectedExecutor)
            || !string.Equals(expectedExecutor, completed.ExecutorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Branch '{completed.Branch}' cannot be emitted by executor '{completed.ExecutorId}'.");
        }
    }
}

internal sealed partial class DecisionSnapshotWorkflowExecutor : Executor
{
    private readonly DecisionSnapshotBuilder _builder;

    public DecisionSnapshotWorkflowExecutor(
        ReleaseRevisionKey releaseRevision,
        IApplicationDataService dataService,
        TimeProvider timeProvider)
        : base(ReleaseWorkflowExecutorIds.DecisionSnapshotBuilder)
    {
        _builder = new DecisionSnapshotBuilder(releaseRevision, dataService, timeProvider);
    }

    [MessageHandler]
    private async ValueTask<ApprovalRequest> BuildAsync(
        BuildDecisionSnapshot command,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var built = await _builder.BuildAsync(command.Round, cancellationToken);
        return new ApprovalRequest(built.Snapshot, built.Request);
    }
}

internal sealed partial class RemediationWorkflowExecutor : Executor
{
    private readonly RemediationHandler _handler;

    public RemediationWorkflowExecutor(
        ReleaseRevisionKey releaseRevision,
        IApplicationDataService dataService)
        : base(ReleaseWorkflowExecutorIds.RemediationHandler)
    {
        _handler = new RemediationHandler(releaseRevision, dataService);
    }

    [MessageHandler(Send = [typeof(EvaluationRoundStart)])]
    private ValueTask<EvaluationRoundStart> HandleAsync(
        RemediationWorkflowResponse response,
        IWorkflowContext context,
        CancellationToken cancellationToken) =>
        new(_handler.HandleAsync(response, cancellationToken));
}

internal sealed partial class HumanDecisionWorkflowExecutor : Executor
{
    private readonly HumanDecisionHandler _handler;

    public HumanDecisionWorkflowExecutor(
        ReleaseRevisionKey releaseRevision,
        IApplicationDataService dataService)
        : base(ReleaseWorkflowExecutorIds.HumanDecisionHandler)
    {
        _handler = new HumanDecisionHandler(releaseRevision, dataService);
    }

    [MessageHandler]
    private ValueTask<PersistedHumanResponse> HandleAsync(
        ApprovalResponse response,
        IWorkflowContext context,
        CancellationToken cancellationToken) =>
        new(_handler.HandleAsync(response.Response, cancellationToken));
}

public sealed partial class ApprovalCompletionExecutor : Executor
{
    public ApprovalCompletionExecutor()
        : base(ReleaseWorkflowExecutorIds.ApprovalCompletion)
    {
    }

    [MessageHandler]
    private PersistedHumanResponse Complete(
        PersistedHumanResponse response,
        IWorkflowContext context) => response;
}
