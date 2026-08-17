using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using AgentWorkflow = Microsoft.Agents.AI.Workflows.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Readiness;

internal static class ReadinessWorkflowTestFactory
{
    private static readonly Guid TestEvidenceId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecurityEvidenceId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ChangeEvidenceId = new("33333333-3333-3333-3333-333333333333");

    public static AgentWorkflow CreateForPlan(EvaluationRoundPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var submission = ContractSubmission();
        var timeProvider = new FixedTimeProvider(submission.SubmittedAt.Value);
        return ReleaseWorkflowFactory.Create(
            submission,
            new ReadinessWorkflowDependencies(
                TestProvider(submission, plan.Test.SimulatedOutcome),
                new TestReadinessPolicy(timeProvider),
                SecurityProvider(submission, plan.Security.SimulatedOutcome),
                new SecurityReadinessPolicy(timeProvider),
                ChangeProvider(submission, plan.Change.SimulatedOutcome),
                new ChangeReadinessPolicy()));
    }

    public static AgentWorkflow CreateWithTest(
        ReleaseSubmission submission,
        ITestEvidenceProvider provider,
        ITestReadinessPolicy policy) =>
        Create(submission, testProvider: provider, testPolicy: policy);

    public static AgentWorkflow CreateWithSecurity(
        ReleaseSubmission submission,
        ISecurityEvidenceProvider provider,
        ISecurityReadinessPolicy policy) =>
        Create(submission, securityProvider: provider, securityPolicy: policy);

    public static AgentWorkflow CreateWithChange(
        ReleaseSubmission submission,
        IChangeEvidenceProvider provider,
        IChangeReadinessPolicy policy) =>
        Create(submission, changeProvider: provider, changePolicy: policy);

    private static AgentWorkflow Create(
        ReleaseSubmission submission,
        ITestEvidenceProvider? testProvider = null,
        ITestReadinessPolicy? testPolicy = null,
        ISecurityEvidenceProvider? securityProvider = null,
        ISecurityReadinessPolicy? securityPolicy = null,
        IChangeEvidenceProvider? changeProvider = null,
        IChangeReadinessPolicy? changePolicy = null)
    {
        var timeProvider = new FixedTimeProvider(submission.SubmittedAt.Value);
        return ReleaseWorkflowFactory.Create(
            submission,
            new ReadinessWorkflowDependencies(
                testProvider ?? new SimulatedTestEvidenceProvider(TestEvidence(submission)),
                testPolicy ?? new TestReadinessPolicy(timeProvider),
                securityProvider ?? new SimulatedSecurityEvidenceProvider(SecurityEvidence(submission)),
                securityPolicy ?? new SecurityReadinessPolicy(timeProvider),
                changeProvider ?? new SimulatedChangeEvidenceProvider(ChangeEvidence(submission)),
                changePolicy ?? new ChangeReadinessPolicy()));
    }

    private static ITestEvidenceProvider TestProvider(
        ReleaseSubmission submission,
        BranchOutcome outcome)
    {
        var evidence = TestEvidence(submission, blocked: outcome is BranchOutcome.Blocked);
        return outcome switch
        {
            BranchOutcome.MissingEvidence => new SimulatedTestEvidenceProvider(null),
            BranchOutcome.TransientFailure => new SimulatedTestEvidenceProvider(
                evidence,
                knownTransientFailuresBeforeSuccess: 3),
            BranchOutcome.Blocked or BranchOutcome.Passed => new SimulatedTestEvidenceProvider(evidence),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown Test outcome."),
        };
    }

    private static ISecurityEvidenceProvider SecurityProvider(
        ReleaseSubmission submission,
        BranchOutcome outcome)
    {
        var evidence = SecurityEvidence(submission, blocked: outcome is BranchOutcome.Blocked);
        return outcome switch
        {
            BranchOutcome.MissingEvidence => new SimulatedSecurityEvidenceProvider(null),
            BranchOutcome.TransientFailure => new SimulatedSecurityEvidenceProvider(
                evidence,
                knownTransientFailuresBeforeSuccess: 3),
            BranchOutcome.Blocked or BranchOutcome.Passed => new SimulatedSecurityEvidenceProvider(evidence),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown Security outcome."),
        };
    }

    private static IChangeEvidenceProvider ChangeProvider(
        ReleaseSubmission submission,
        BranchOutcome outcome)
    {
        var evidence = ChangeEvidence(submission, approved: outcome is not BranchOutcome.Blocked);
        return outcome switch
        {
            BranchOutcome.MissingEvidence => new SimulatedChangeEvidenceProvider(null),
            BranchOutcome.TransientFailure => new SimulatedChangeEvidenceProvider(
                evidence,
                knownTransientFailuresBeforeSuccess: 3),
            BranchOutcome.Blocked or BranchOutcome.Passed => new SimulatedChangeEvidenceProvider(evidence),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown Change outcome."),
        };
    }

    private static TestEvidenceRecord TestEvidence(
        ReleaseSubmission submission,
        bool blocked = false) => new(
        TestEvidenceId,
        submission.Key,
        1,
        submission.SubmittedAt,
        null,
        submission.ReleaseVersion,
        submission.SubmittedAt,
        blocked ? 0m : 0.98m,
        []);

    private static SecurityEvidenceRecord SecurityEvidence(
        ReleaseSubmission submission,
        bool blocked = false) => new(
        SecurityEvidenceId,
        submission.Key,
        1,
        submission.SubmittedAt,
        null,
        submission.ReleaseVersion,
        submission.SubmittedAt,
        blocked ? ["CRITICAL-1"] : [],
        [],
        new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>());

    private static ChangeEvidenceRecord ChangeEvidence(
        ReleaseSubmission submission,
        bool approved = true) => new(
        ChangeEvidenceId,
        submission.Key,
        1,
        submission.SubmittedAt,
        null,
        isApproved: approved,
        submission.RequestedDeploymentWindow);

    private static ReleaseSubmission ContractSubmission() => new(
        new ReleaseRevisionKey("workflow-contract", 1),
        "orders",
        "2.4.0",
        new UtcInterval(
            new UtcInstant(new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero)),
            new UtcInstant(new DateTimeOffset(2026, 8, 17, 11, 0, 0, TimeSpan.Zero))),
        new UtcInstant(new DateTimeOffset(2026, 8, 17, 7, 0, 0, TimeSpan.Zero)));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
