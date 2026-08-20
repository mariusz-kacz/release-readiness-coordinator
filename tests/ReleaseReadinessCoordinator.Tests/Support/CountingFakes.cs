using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Support;

internal sealed class CountingFakes
{
    private readonly int[] _providerCalls = new int[4];
    private readonly int[] _policyCalls = new int[4];
    private readonly int[] _transientFailuresRemaining = new int[4];
    private readonly bool[] _unexpectedFailures = new bool[4];

    public void Configure(BranchOutcome test, BranchOutcome security, BranchOutcome change)
    {
        Configure(ReadinessCheck.Test, test);
        Configure(ReadinessCheck.Security, security);
        Configure(ReadinessCheck.Change, change);
    }

    public void FailUnexpectedly(ReadinessCheck check) =>
        _unexpectedFailures[(int)check] = true;

    public ReadinessWorkflowDependencies CreateDependencies(
        IApplicationDataService dataService,
        TimeProvider timeProvider) => new(
        new CountingTestProvider(this, dataService),
        new CountingTestPolicy(this, new TestReadinessPolicy()),
        new CountingSecurityProvider(this, dataService),
        new CountingSecurityPolicy(this, new SecurityReadinessPolicy()),
        new CountingChangeProvider(this, dataService),
        new CountingChangePolicy(this, new ChangeReadinessPolicy()));

    public void AssertCounts(
        (int Provider, int Policy) test,
        (int Provider, int Policy) security,
        (int Provider, int Policy) change)
    {
        Assert.Equal(test, Counts(ReadinessCheck.Test));
        Assert.Equal(security, Counts(ReadinessCheck.Security));
        Assert.Equal(change, Counts(ReadinessCheck.Change));
    }

    public (int Provider, int Policy) CountsFor(ReadinessCheck check) => Counts(check);

    private void Configure(ReadinessCheck check, BranchOutcome outcome)
    {
        _transientFailuresRemaining[(int)check] =
            outcome is BranchOutcome.TransientFailure ? 3 : 0;
    }

    private (int Provider, int Policy) Counts(ReadinessCheck check) =>
        (Volatile.Read(ref _providerCalls[(int)check]), Volatile.Read(ref _policyCalls[(int)check]));

    private async ValueTask<TEvidence?> GetAsync<TEvidence>(
        ReadinessCheck check,
        EvidenceKind kind,
        IApplicationDataService dataService,
        ReleaseId releaseId,
        CancellationToken cancellationToken)
        where TEvidence : EvidenceRecord
    {
        Interlocked.Increment(ref _providerCalls[(int)check]);
        var evidence = (TEvidence?)await dataService.GetCurrentEvidenceAsync(
            releaseId,
            kind,
            cancellationToken);
        if (_unexpectedFailures[(int)check])
        {
            throw new UnexpectedEvidenceProviderException($"Unexpected {check} provider failure.");
        }

        if (Interlocked.Decrement(ref _transientFailuresRemaining[(int)check]) >= 0)
        {
            throw new KnownTransientEvidenceProviderException(
                evidence?.Id ?? throw new InvalidOperationException(
                    $"Transient {check} failure requires current evidence."),
                $"Deterministic {check} source failure.");
        }

        return evidence;
    }

    private sealed class UnexpectedEvidenceProviderException(string message) : Exception(message);

    private TEvaluation Evaluate<TEvidence, TEvaluation>(
        ReadinessCheck check,
        ReleaseSubmission submission,
        TEvidence evidence,
        Func<ReleaseSubmission, TEvidence, TEvaluation> evaluate)
        where TEvidence : EvidenceRecord
    {
        Interlocked.Increment(ref _policyCalls[(int)check]);
        return evaluate(submission, evidence);
    }

    private sealed class CountingTestProvider(
        CountingFakes owner,
        IApplicationDataService dataService) : ITestEvidenceProvider
    {
        public ValueTask<TestEvidenceRecord?> GetCurrentAsync(
            ReleaseId releaseId,
            CancellationToken cancellationToken) =>
            owner.GetAsync<TestEvidenceRecord>(
                ReadinessCheck.Test,
                EvidenceKind.Test,
                dataService,
                releaseId,
                cancellationToken);
    }

    private sealed class CountingSecurityProvider(
        CountingFakes owner,
        IApplicationDataService dataService) : ISecurityEvidenceProvider
    {
        public ValueTask<SecurityEvidenceRecord?> GetCurrentAsync(
            ReleaseId releaseId,
            CancellationToken cancellationToken) =>
            owner.GetAsync<SecurityEvidenceRecord>(
                ReadinessCheck.Security,
                EvidenceKind.Security,
                dataService,
                releaseId,
                cancellationToken);
    }

    private sealed class CountingChangeProvider(
        CountingFakes owner,
        IApplicationDataService dataService) : IChangeEvidenceProvider
    {
        public ValueTask<ChangeEvidenceRecord?> GetCurrentAsync(
            ReleaseId releaseId,
            CancellationToken cancellationToken) =>
            owner.GetAsync<ChangeEvidenceRecord>(
                ReadinessCheck.Change,
                EvidenceKind.Change,
                dataService,
                releaseId,
                cancellationToken);
    }

    private sealed class CountingTestPolicy(
        CountingFakes owner,
        ITestReadinessPolicy inner) : ITestReadinessPolicy
    {
        public TestPolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            TestEvidenceRecord evidence) =>
            owner.Evaluate(ReadinessCheck.Test, submission, evidence, inner.Evaluate);
    }

    private sealed class CountingSecurityPolicy(
        CountingFakes owner,
        ISecurityReadinessPolicy inner) : ISecurityReadinessPolicy
    {
        public SecurityPolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            SecurityEvidenceRecord evidence) =>
            owner.Evaluate(ReadinessCheck.Security, submission, evidence, inner.Evaluate);
    }

    private sealed class CountingChangePolicy(
        CountingFakes owner,
        IChangeReadinessPolicy inner) : IChangeReadinessPolicy
    {
        public ChangePolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            ChangeEvidenceRecord evidence) =>
            owner.Evaluate(ReadinessCheck.Change, submission, evidence, inner.Evaluate);
    }
}
