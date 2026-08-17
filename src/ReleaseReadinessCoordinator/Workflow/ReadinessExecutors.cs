using System.Collections.ObjectModel;
using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using DomainBranchResult = ReleaseReadinessCoordinator.Domain.BranchResult;
using DomainBranchWorkItem = ReleaseReadinessCoordinator.Domain.BranchWorkItem;

namespace ReleaseReadinessCoordinator.Workflow;

public static class ReleaseWorkflowExecutorIds
{
    public const string Planner = "readiness-planner";
    public const string Test = "test-readiness";
    public const string Security = "security-readiness";
    public const string Change = "change-readiness";
    public const string Aggregator = "readiness-aggregator";
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

public sealed partial class ReadinessPlanner : Executor
{
    public ReadinessPlanner()
        : base(ReleaseWorkflowExecutorIds.Planner)
    {
    }

    [MessageHandler(Send = [typeof(BranchWorkItem)])]
    private async ValueTask PlanAsync(
        EvaluationRoundPlan plan,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        foreach (var branch in ReleaseWorkflowExecutorIds.Branches.Keys)
        {
            var branchPlan = plan.For(branch);
            if (!Enum.IsDefined(branchPlan.Disposition))
            {
                throw new InvalidOperationException(
                    $"Branch '{branch}' has impossible disposition '{branchPlan.Disposition}'.");
            }

            if (!Enum.IsDefined(branchPlan.SimulatedOutcome))
            {
                throw new InvalidOperationException(
                    $"Branch '{branch}' has impossible simulated outcome '{branchPlan.SimulatedOutcome}'.");
            }

            await context.SendMessageAsync(
                new BranchWorkItem(
                    plan.RoundNumber,
                    branch,
                    branchPlan.Disposition,
                    branchPlan.SimulatedOutcome),
                cancellationToken);
        }
    }

    [MessageHandler(Send = [typeof(BranchWorkItem)])]
    private ValueTask PlanAfterRemediationAsync(
        RemediationResponse response,
        IWorkflowContext context,
        CancellationToken cancellationToken) =>
        PlanAsync(response.NextRound, context, cancellationToken);
}

public sealed partial class ReadinessBranchExecutor : Executor
{
    private readonly ReadinessBranch _branch;
    private readonly ReadinessCheck _check;
    private readonly ReleaseSubmission _submission;
    private readonly Func<
        ReleaseSubmission,
        DomainBranchWorkItem,
        CancellationToken,
        Task<DomainBranchResult>> _execute;

    internal ReadinessBranchExecutor(
        ReleaseSubmission submission,
        ITestEvidenceProvider testEvidenceProvider,
        ITestReadinessPolicy testPolicy)
        : base(ReleaseWorkflowExecutorIds.Test)
    {
        _branch = ReadinessBranch.Test;
        _check = ReadinessCheck.Test;
        _submission = submission ?? throw new ArgumentNullException(nameof(submission));
        _execute = new TestReadinessBranchExecutor(
            testEvidenceProvider,
            testPolicy).ExecuteAsync;
    }

    internal ReadinessBranchExecutor(
        ReleaseSubmission submission,
        ISecurityEvidenceProvider securityEvidenceProvider,
        ISecurityReadinessPolicy securityPolicy)
        : base(ReleaseWorkflowExecutorIds.Security)
    {
        _branch = ReadinessBranch.Security;
        _check = ReadinessCheck.Security;
        _submission = submission ?? throw new ArgumentNullException(nameof(submission));
        _execute = new SecurityReadinessBranchExecutor(
            securityEvidenceProvider,
            securityPolicy).ExecuteAsync;
    }

    internal ReadinessBranchExecutor(
        ReleaseSubmission submission,
        IChangeEvidenceProvider changeEvidenceProvider,
        IChangeReadinessPolicy changePolicy)
        : base(ReleaseWorkflowExecutorIds.Change)
    {
        _branch = ReadinessBranch.Change;
        _check = ReadinessCheck.Change;
        _submission = submission ?? throw new ArgumentNullException(nameof(submission));
        _execute = new ChangeReadinessBranchExecutor(
            changeEvidenceProvider,
            changePolicy).ExecuteAsync;
    }

    [MessageHandler]
    private async ValueTask<BranchResult> EvaluateAsync(
        BranchWorkItem workItem,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (workItem.Branch != _branch)
        {
            throw new InvalidOperationException(
                $"Executor '{Id}' cannot process branch '{workItem.Branch}'.");
        }

        if (!Enum.IsDefined(workItem.Disposition))
        {
            throw new InvalidOperationException(
                $"Branch '{workItem.Branch}' has impossible disposition '{workItem.Disposition}'.");
        }

        if (!Enum.IsDefined(workItem.SimulatedOutcome))
        {
            throw new InvalidOperationException(
                $"Branch '{workItem.Branch}' has impossible simulated outcome '{workItem.SimulatedOutcome}'.");
        }

        return await ExecuteRealAsync(workItem, cancellationToken);
    }

    private async ValueTask<BranchResult> ExecuteRealAsync(
        BranchWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (workItem.Disposition is not BranchDisposition.Execute)
        {
            throw new InvalidOperationException(
                $"Real {_check} readiness execution cannot process reuse work before the separate reuse path is implemented.");
        }

        var evaluation = await _execute(
            _submission,
            new DomainBranchWorkItem(
                _submission.Key,
                workItem.RoundNumber,
                _check,
                WorkDisposition.Execute,
                workItem.RoundNumber == 1
                    ? PlanningReason.InitialEvaluation
                    : PlanningReason.PreviousResultNotPassed,
                workItem.RoundNumber == 1
                    ? "Executed because this is the initial evaluation."
                    : $"Executed because the previous {_check} result did not pass."),
            cancellationToken);
        return new BranchResult(
            workItem.RoundNumber,
            _branch,
            workItem.Disposition,
            Id,
            evaluation.Outcome,
            evaluation);
    }
}

public sealed partial class ReadinessAggregator : Executor, IResettableExecutor
{
    private readonly List<BranchResult> _results = [];

    public ReadinessAggregator()
        : base(ReleaseWorkflowExecutorIds.Aggregator)
    {
    }

    [MessageHandler(
        Send = [typeof(RemediationRequest), typeof(ApprovalRequest)],
        Yield = [typeof(EvaluationRoundResult)])]
    private async ValueTask ReceiveAsync(
        BranchResult result,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(result);

        if (_results.Any(existing => existing.Branch == result.Branch))
        {
            throw new InvalidOperationException(
                $"Duplicate result for readiness branch '{result.Branch}'.");
        }

        _results.Add(result);
        if (_results.Count == ReleaseWorkflowExecutorIds.Branches.Count)
        {
            var round = Aggregate(_results);
            await context.YieldOutputAsync(round, cancellationToken);
            if (round.Results.All(result => result.Outcome is BranchOutcome.Passed))
            {
                await context.SendMessageAsync(
                    new ApprovalRequest(round.RoundNumber),
                    cancellationToken);
            }
            else
            {
                await context.SendMessageAsync(
                    new RemediationRequest(round.RoundNumber),
                    cancellationToken);
            }

            _results.Clear();
        }
    }

    public ValueTask ResetAsync()
    {
        _results.Clear();
        return ValueTask.CompletedTask;
    }

    internal static EvaluationRoundResult Aggregate(IReadOnlyCollection<BranchResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count != ReleaseWorkflowExecutorIds.Branches.Count)
        {
            throw new InvalidOperationException(
                $"A complete evaluation round requires exactly {ReleaseWorkflowExecutorIds.Branches.Count} branch results.");
        }

        foreach (var result in results)
        {
            ValidateIdentity(result);
        }

        var duplicates = results
            .GroupBy(result => result.Branch)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate results for readiness branches: {string.Join(", ", duplicates)}.");
        }

        var roundNumber = results.First().RoundNumber;
        if (results.Any(result => result.RoundNumber != roundNumber))
        {
            throw new InvalidOperationException("Branch results belong to different evaluation rounds.");
        }

        return new EvaluationRoundResult(
            roundNumber,
            results.OrderBy(result => result.Branch).ToArray());
    }

    private static void ValidateIdentity(BranchResult result)
    {
        if (!ReleaseWorkflowExecutorIds.Branches.TryGetValue(result.Branch, out var expectedExecutorId))
        {
            throw new InvalidOperationException(
                $"Impossible readiness branch identity '{result.Branch}'.");
        }

        if (!string.Equals(result.ExecutorId, expectedExecutorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Branch '{result.Branch}' cannot be emitted by executor '{result.ExecutorId}'.");
        }

        if (!Enum.IsDefined(result.Disposition))
        {
            throw new InvalidOperationException(
                $"Branch '{result.Branch}' has impossible disposition '{result.Disposition}'.");
        }

        if (!Enum.IsDefined(result.Outcome))
        {
            throw new InvalidOperationException(
                $"Branch '{result.Branch}' has impossible outcome '{result.Outcome}'.");
        }
    }
}

public sealed partial class ApprovalCompletionExecutor : Executor
{
    public ApprovalCompletionExecutor()
        : base(ReleaseWorkflowExecutorIds.ApprovalCompletion)
    {
    }

    [MessageHandler]
    private ApprovalResponse Complete(ApprovalResponse response, IWorkflowContext context) => response;
}
