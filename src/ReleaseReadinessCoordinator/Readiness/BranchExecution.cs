using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal interface IEvidenceProvider<TEvidence>
    where TEvidence : EvidenceRecord
{
    ValueTask<TEvidence?> GetCurrentAsync(
        ReleaseRevisionKey releaseRevision,
        CancellationToken cancellationToken);
}

internal sealed class KnownTransientEvidenceProviderException : Exception
{
    public KnownTransientEvidenceProviderException(Guid evidenceId, string message)
        : base(message)
    {
        if (evidenceId == Guid.Empty)
        {
            throw new ArgumentException(
                "A known transient provider failure must identify the evidence being retrieved.",
                nameof(evidenceId));
        }

        EvidenceId = evidenceId;
    }

    public Guid EvidenceId { get; }
}

internal interface IReadinessPolicyEvaluation
{
    BranchOutcome Outcome { get; }

    UtcInstant? ValidUntil { get; }

    ImmutableDictionary<string, string> Findings { get; }
}

internal sealed class TestReadinessBranchExecutor
{
    private readonly ITestEvidenceProvider _provider;
    private readonly ITestReadinessPolicy _policy;

    public TestReadinessBranchExecutor(
        ITestEvidenceProvider provider,
        ITestReadinessPolicy policy)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public Task<BranchResult> ExecuteAsync(
        ReleaseSubmission submission,
        BranchWorkItem workItem,
        CancellationToken cancellationToken = default) =>
        BranchExecution.ExecuteAsync(
            submission,
            workItem,
            ReadinessCheck.Test,
            EvidenceKind.Test,
            _provider,
            _policy.Version,
            _policy.Evaluate,
            cancellationToken);
}

internal sealed class SecurityReadinessBranchExecutor
{
    private readonly ISecurityEvidenceProvider _provider;
    private readonly ISecurityReadinessPolicy _policy;

    public SecurityReadinessBranchExecutor(
        ISecurityEvidenceProvider provider,
        ISecurityReadinessPolicy policy)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public Task<BranchResult> ExecuteAsync(
        ReleaseSubmission submission,
        BranchWorkItem workItem,
        CancellationToken cancellationToken = default) =>
        BranchExecution.ExecuteAsync(
            submission,
            workItem,
            ReadinessCheck.Security,
            EvidenceKind.Security,
            _provider,
            _policy.Version,
            _policy.Evaluate,
            cancellationToken);
}

internal static class BranchExecution
{
    public static async Task<BranchResult> ExecuteAsync<TEvidence, TEvaluation>(
        ReleaseSubmission submission,
        BranchWorkItem workItem,
        ReadinessCheck check,
        EvidenceKind evidenceKind,
        IEvidenceProvider<TEvidence> provider,
        string policyVersion,
        Func<ReleaseSubmission, TEvidence, TEvaluation> evaluate,
        CancellationToken cancellationToken)
        where TEvidence : EvidenceRecord
        where TEvaluation : IReadinessPolicyEvaluation
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(evaluate);
        ValidateWork(submission, workItem, check);

        var retrieval = await EvidenceProviderRetry.GetCurrentAsync(
            provider,
            workItem.ReleaseRevision,
            cancellationToken);
        if (retrieval.ExhaustedEvidenceId.HasValue)
        {
            return Result(
                workItem,
                check,
                evidenceKind,
                policyVersion,
                BranchOutcome.TransientFailure,
                retrieval.ExhaustedEvidenceId,
                validUntil: null,
                retrieval.Attempts,
                new Dictionary<string, string>
                {
                    ["provider"] = $"{check} evidence retrieval exhausted {retrieval.Attempts.Length} attempts.",
                });
        }

        if (retrieval.Evidence is null)
        {
            return Result(
                workItem,
                check,
                evidenceKind,
                policyVersion,
                BranchOutcome.MissingEvidence,
                evidenceId: null,
                validUntil: null,
                retrieval.Attempts,
                new Dictionary<string, string>
                {
                    ["evidence"] = $"No current {check} evidence record was found.",
                });
        }

        if (retrieval.Evidence.ReleaseRevision != workItem.ReleaseRevision)
        {
            throw new InvalidOperationException(
                $"The {check} evidence provider returned evidence for a different release revision.");
        }

        var evaluation = evaluate(submission, retrieval.Evidence);
        return Result(
            workItem,
            check,
            evidenceKind,
            policyVersion,
            evaluation.Outcome,
            retrieval.Evidence.Id,
            evaluation.ValidUntil,
            retrieval.Attempts,
            evaluation.Findings);
    }

    private static BranchResult Result(
        BranchWorkItem workItem,
        ReadinessCheck check,
        EvidenceKind evidenceKind,
        string policyVersion,
        BranchOutcome outcome,
        Guid? evidenceId,
        UtcInstant? validUntil,
        IEnumerable<string> attempts,
        IReadOnlyDictionary<string, string> findings) => new(
            Guid.NewGuid(),
            workItem.ReleaseRevision,
            workItem.RoundNumber,
            check,
            outcome,
            ExecutionDisposition.Executed,
            workItem.PlanningReason,
            workItem.PlanningDetail,
            evidenceId,
            evidenceKind,
            policyVersion,
            validUntil,
            attempts,
            findings,
            reuseSourceResultId: null,
            reuseSourceRound: null);

    private static void ValidateWork(
        ReleaseSubmission submission,
        BranchWorkItem workItem,
        ReadinessCheck check)
    {
        if (submission.Key != workItem.ReleaseRevision)
        {
            throw new InvalidOperationException(
                $"The {check} work item and release submission identify different revisions.");
        }

        if (workItem.Check != check)
        {
            throw new InvalidOperationException(
                $"The {check} readiness executor cannot process '{workItem.Check}' work.");
        }

        if (workItem.Disposition is not WorkDisposition.Execute)
        {
            throw new InvalidOperationException(
                $"The {check} readiness executor processes only Execute work; reuse is a separate path.");
        }
    }
}

internal static class EvidenceProviderRetry
{
    private const int MaximumAttempts = 3;

    public static async ValueTask<EvidenceRetrieval<TEvidence>> GetCurrentAsync<TEvidence>(
        IEvidenceProvider<TEvidence> provider,
        ReleaseRevisionKey releaseRevision,
        CancellationToken cancellationToken)
        where TEvidence : EvidenceRecord
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(releaseRevision);

        var attempts = ImmutableArray.CreateBuilder<string>(MaximumAttempts);
        Guid? failedEvidenceId = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                var evidence = await provider.GetCurrentAsync(releaseRevision, cancellationToken);
                attempts.Add($"Attempt {attempt} succeeded.");
                return new EvidenceRetrieval<TEvidence>(evidence, null, attempts.ToImmutable());
            }
            catch (KnownTransientEvidenceProviderException exception)
            {
                if (failedEvidenceId.HasValue && failedEvidenceId.Value != exception.EvidenceId)
                {
                    throw new InvalidOperationException(
                        "Immediate retries identified different evidence records.",
                        exception);
                }

                failedEvidenceId = exception.EvidenceId;
                attempts.Add($"Attempt {attempt} known transient failure: {exception.Message}");
            }
        }

        return new EvidenceRetrieval<TEvidence>(
            Evidence: null,
            failedEvidenceId,
            attempts.ToImmutable());
    }
}

internal sealed record EvidenceRetrieval<TEvidence>(
    TEvidence? Evidence,
    Guid? ExhaustedEvidenceId,
    ImmutableArray<string> Attempts)
    where TEvidence : EvidenceRecord;
