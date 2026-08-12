namespace ReleaseReadinessCoordinator.Workflow;

public enum ReadinessBranch
{
    Test = 1,
    Security = 2,
    Change = 3,
    Dependency = 4,
}

public enum BranchDisposition
{
    Execute = 1,
    Reuse = 2,
}

public sealed record EvaluationRoundPlan(
    int RoundNumber,
    BranchDisposition Test,
    BranchDisposition Security,
    BranchDisposition Change,
    BranchDisposition Dependency)
{
    public BranchDisposition DispositionFor(ReadinessBranch branch) => branch switch
    {
        ReadinessBranch.Test => Test,
        ReadinessBranch.Security => Security,
        ReadinessBranch.Change => Change,
        ReadinessBranch.Dependency => Dependency,
        _ => throw new InvalidOperationException($"Unknown readiness branch '{branch}'."),
    };
}

public sealed record BranchWorkItem(
    int RoundNumber,
    ReadinessBranch Branch,
    BranchDisposition Disposition);

public sealed record BranchResult(
    int RoundNumber,
    ReadinessBranch Branch,
    BranchDisposition Disposition,
    string ExecutorId);

public sealed record EvaluationRoundResult(
    int RoundNumber,
    IReadOnlyList<BranchResult> Results);
