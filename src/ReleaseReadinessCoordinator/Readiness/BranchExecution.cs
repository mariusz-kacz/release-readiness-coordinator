using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal interface IEvidenceProvider<TEvidence>
    where TEvidence : EvidenceRecord
{
    ValueTask<TEvidence?> GetCurrentAsync(
        ReleaseId releaseId,
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

    ImmutableDictionary<string, string> Findings { get; }
}

internal static class BranchExecution
{
    public static async Task<BranchResult> ExecuteAsync<TEvidence, TEvaluation>(
        Guid resultId,
        ReleaseSubmission submission,
        BranchWorkItem workItem,
        ReadinessCheck check,
        EvidenceKind evidenceKind,
        IEvidenceProvider<TEvidence> provider,
        Func<ReleaseSubmission, TEvidence, TEvaluation> evaluate,
        CancellationToken cancellationToken)
        where TEvidence : EvidenceRecord
        where TEvaluation : IReadinessPolicyEvaluation
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(evaluate);
        if (resultId == Guid.Empty)
        {
            throw new ArgumentException("A result ID cannot be empty.", nameof(resultId));
        }

        ValidateWork(submission, workItem, check);

        var retrieval = await EvidenceProviderRetry.GetCurrentAsync(
            provider,
            workItem.ReleaseId,
            cancellationToken);
        if (retrieval.ExhaustedEvidenceId.HasValue)
        {
            return Result(
                resultId,
                workItem,
                check,
                evidenceKind,
                BranchOutcome.TransientFailure,
                retrieval.ExhaustedEvidenceId,
                retrieval.Attempts,
                new Dictionary<string, string>
                {
                    ["provider"] = $"{check} evidence retrieval exhausted {retrieval.Attempts.Length} attempts.",
                });
        }

        if (retrieval.Evidence is null)
        {
            return Result(
                resultId,
                workItem,
                check,
                evidenceKind,
                BranchOutcome.MissingEvidence,
                evidenceId: null,
                retrieval.Attempts,
                new Dictionary<string, string>
                {
                    ["evidence"] = $"No current {check} evidence record was found.",
                });
        }

        if (retrieval.Evidence.ReleaseId != workItem.ReleaseId)
        {
            throw new InvalidOperationException(
                $"The {check} evidence provider returned evidence for a different release.");
        }

        var evaluation = evaluate(submission, retrieval.Evidence);
        return Result(
            resultId,
            workItem,
            check,
            evidenceKind,
            evaluation.Outcome,
            retrieval.Evidence.Id,
            retrieval.Attempts,
            evaluation.Findings);
    }

    private static BranchResult Result(
        Guid resultId,
        BranchWorkItem workItem,
        ReadinessCheck check,
        EvidenceKind evidenceKind,
        BranchOutcome outcome,
        Guid? evidenceId,
        IEnumerable<string> attempts,
        IReadOnlyDictionary<string, string> findings) => new(
            resultId,
            workItem.ReleaseId,
            workItem.RoundNumber,
            check,
            outcome,
            ExecutionDisposition.Executed,
            workItem.PlanningReason,
            workItem.PlanningDetail,
            evidenceId,
            evidenceKind,
            attempts,
            findings,
            reuseSourceResultId: null,
            reuseSourceRound: null);

    private static void ValidateWork(
        ReleaseSubmission submission,
        BranchWorkItem workItem,
        ReadinessCheck check)
    {
        if (submission.ReleaseId != workItem.ReleaseId)
        {
            throw new InvalidOperationException(
                $"The {check} work item and release submission identify different releases.");
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
        ReleaseId releaseId,
        CancellationToken cancellationToken)
        where TEvidence : EvidenceRecord
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(releaseId);

        var attempts = ImmutableArray.CreateBuilder<string>(MaximumAttempts);
        Guid? failedEvidenceId = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                var evidence = await provider.GetCurrentAsync(releaseId, cancellationToken);
                attempts.Add(SuccessfulAttemptDetail(attempt));
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

    internal static string SuccessfulAttemptDetail(int attempt)
    {
        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        return attempt == 1
            ? "Check completed on the first attempt."
            : $"Check completed on attempt {attempt}.";
    }
}

internal sealed record EvidenceRetrieval<TEvidence>(
    TEvidence? Evidence,
    Guid? ExhaustedEvidenceId,
    ImmutableArray<string> Attempts)
    where TEvidence : EvidenceRecord;
