using System.Collections.ObjectModel;
using Microsoft.Agents.AI.Workflows;

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
        if (!Enum.IsDefined(plan.WaitKind))
        {
            throw new InvalidOperationException($"Impossible external wait kind '{plan.WaitKind}'.");
        }

        foreach (var branch in ReleaseWorkflowExecutorIds.Branches.Keys)
        {
            var disposition = plan.DispositionFor(branch);
            if (!Enum.IsDefined(disposition))
            {
                throw new InvalidOperationException(
                    $"Branch '{branch}' has impossible disposition '{disposition}'.");
            }

            await context.SendMessageAsync(
                new BranchWorkItem(plan.RoundNumber, branch, disposition, plan.WaitKind),
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

    public ReadinessBranchExecutor(ReadinessBranch branch, string id)
        : base(id)
    {
        _branch = branch;
    }

    [MessageHandler]
    private BranchResult Evaluate(BranchWorkItem workItem, IWorkflowContext context)
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

        if (!Enum.IsDefined(workItem.WaitKind))
        {
            throw new InvalidOperationException(
                $"Branch '{workItem.Branch}' has impossible external wait kind '{workItem.WaitKind}'.");
        }

        return new BranchResult(workItem.RoundNumber, _branch, workItem.Disposition, Id, workItem.WaitKind);
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
            await context.SendMessageAsync(
                round.WaitKind switch
                {
                    ExternalWaitKind.Remediation => new RemediationRequest(round.RoundNumber),
                    ExternalWaitKind.Approval => new ApprovalRequest(round.RoundNumber),
                    _ => throw new InvalidOperationException(
                        $"Impossible external wait kind '{round.WaitKind}'."),
                },
                cancellationToken);
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

        var waitKind = results.First().WaitKind;
        if (!Enum.IsDefined(waitKind) || results.Any(result => result.WaitKind != waitKind))
        {
            throw new InvalidOperationException("Branch results contain inconsistent external wait kinds.");
        }

        return new EvaluationRoundResult(
            roundNumber,
            results.OrderBy(result => result.Branch).ToArray(),
            waitKind);
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
