using ReleaseReadinessCoordinator.Domain;
using DomainBranchResult = ReleaseReadinessCoordinator.Domain.BranchResult;

namespace ReleaseReadinessCoordinator.Workflow;

public enum ReadinessBranch
{
    Test = 1,
    Security = 2,
    Change = 3,
}

public enum BranchDisposition
{
    Execute = 1,
    Reuse = 2,
}

public enum ExternalWaitKind
{
    Remediation = 1,
    Approval = 2,
}

public sealed record BranchPlan(
    BranchDisposition Disposition,
    BranchOutcome SimulatedOutcome);

public sealed record EvaluationRoundPlan(
    int RoundNumber,
    BranchPlan Test,
    BranchPlan Security,
    BranchPlan Change)
{
    public BranchPlan For(ReadinessBranch branch) => branch switch
    {
        ReadinessBranch.Test => Test,
        ReadinessBranch.Security => Security,
        ReadinessBranch.Change => Change,
        _ => throw new InvalidOperationException($"Unknown readiness branch '{branch}'."),
    };
}

public sealed record BranchWorkItem(
    int RoundNumber,
    ReadinessBranch Branch,
    BranchDisposition Disposition,
    BranchOutcome SimulatedOutcome);

public sealed record BranchResult(
    int RoundNumber,
    ReadinessBranch Branch,
    BranchDisposition Disposition,
    string ExecutorId,
    BranchOutcome Outcome,
    DomainBranchResult? Evaluation = null);

public sealed record EvaluationRoundResult(
    int RoundNumber,
    IReadOnlyList<BranchResult> Results);

public sealed record RemediationRequest(int RoundNumber);

public sealed record RemediationResponse(EvaluationRoundPlan NextRound);

public sealed record ApprovalRequest(int RoundNumber);

public sealed record ApprovalResponse(bool Approved);
