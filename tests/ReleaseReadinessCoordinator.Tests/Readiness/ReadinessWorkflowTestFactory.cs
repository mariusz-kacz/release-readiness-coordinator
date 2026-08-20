using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using AgentWorkflow = Microsoft.Agents.AI.Workflows.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Readiness;

internal sealed class ReadinessWorkflowTestHost : IAsyncDisposable
{
    private static readonly Guid TestEvidenceId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecurityEvidenceId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ChangeEvidenceId = new("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly ReadinessWorkflowDependencies _dependencies;
    private readonly TimeProvider _timeProvider;

    private ReadinessWorkflowTestHost(
        SqliteConnection connection,
        AppDbContext context,
        ReleaseSubmission submission,
        ApplicationDataService dataService,
        TimeProvider timeProvider,
        ReadinessWorkflowDependencies dependencies,
        EvaluationRoundStart input)
    {
        _connection = connection;
        _context = context;
        _dependencies = dependencies;
        _timeProvider = timeProvider;
        Submission = submission;
        DataService = dataService;
        Input = input;
    }

    public ReleaseSubmission Submission { get; }

    public IApplicationDataService DataService { get; }

    public EvaluationRoundStart Input { get; }

    public AgentWorkflow CreateWorkflow() =>
        ReleaseWorkflowFactory.Create(
            Submission,
            DataService,
            _timeProvider,
            _dependencies);

    public static Task<ReadinessWorkflowTestHost> CreateForOutcomesAsync(
        BranchOutcome testOutcome,
        BranchOutcome securityOutcome = BranchOutcome.Passed,
        BranchOutcome changeOutcome = BranchOutcome.Passed)
    {
        var submission = ContractSubmission();
        var now = submission.RequestedDeploymentWindow.Start;
        var timeProvider = new FixedTimeProvider(now.Value);
        var testEvidence = TestEvidence(submission, blocked: testOutcome is BranchOutcome.Blocked);
        var securityEvidence = SecurityEvidence(
            submission,
            blocked: securityOutcome is BranchOutcome.Blocked);
        var changeEvidence = ChangeEvidence(
            submission,
            approved: changeOutcome is not BranchOutcome.Blocked);
        var evidence = new EvidenceRecord?[]
        {
            testOutcome is BranchOutcome.MissingEvidence ? null : testEvidence,
            securityOutcome is BranchOutcome.MissingEvidence ? null : securityEvidence,
            changeOutcome is BranchOutcome.MissingEvidence ? null : changeEvidence,
        }.OfType<EvidenceRecord>().ToArray();
        var dependencies = new ReadinessWorkflowDependencies(
            TestProvider(testEvidence, testOutcome),
            new TestReadinessPolicy(),
            SecurityProvider(securityEvidence, securityOutcome),
            new SecurityReadinessPolicy(),
            ChangeProvider(changeEvidence, changeOutcome),
            new ChangeReadinessPolicy());

        return CreateAsync(submission, evidence, timeProvider, dependencies);
    }

    public static Task<ReadinessWorkflowTestHost> CreateWithTestAsync(
        ReleaseSubmission submission,
        TestEvidenceRecord evidence,
        ITestEvidenceProvider provider,
        ITestReadinessPolicy policy)
    {
        var timeProvider = new FixedTimeProvider(submission.RequestedDeploymentWindow.Start.Value);
        return CreateAsync(
            submission,
            [evidence, SecurityEvidence(submission), ChangeEvidence(submission)],
            timeProvider,
            new ReadinessWorkflowDependencies(
                provider,
                policy,
                new SimulatedSecurityEvidenceProvider(SecurityEvidence(submission)),
                new SecurityReadinessPolicy(),
                new SimulatedChangeEvidenceProvider(ChangeEvidence(submission)),
                new ChangeReadinessPolicy()));
    }

    public static Task<ReadinessWorkflowTestHost> CreateWithSecurityAsync(
        ReleaseSubmission submission,
        SecurityEvidenceRecord evidence,
        ISecurityEvidenceProvider provider,
        ISecurityReadinessPolicy policy)
    {
        var timeProvider = new FixedTimeProvider(submission.RequestedDeploymentWindow.Start.Value);
        return CreateAsync(
            submission,
            [TestEvidence(submission), evidence, ChangeEvidence(submission)],
            timeProvider,
            new ReadinessWorkflowDependencies(
                new SimulatedTestEvidenceProvider(TestEvidence(submission)),
                new TestReadinessPolicy(),
                provider,
                policy,
                new SimulatedChangeEvidenceProvider(ChangeEvidence(submission)),
                new ChangeReadinessPolicy()));
    }

    public static Task<ReadinessWorkflowTestHost> CreateWithChangeAsync(
        ReleaseSubmission submission,
        ChangeEvidenceRecord evidence,
        IChangeEvidenceProvider provider,
        IChangeReadinessPolicy policy)
    {
        var timeProvider = new FixedTimeProvider(submission.RequestedDeploymentWindow.Start.Value);
        return CreateAsync(
            submission,
            [TestEvidence(submission), SecurityEvidence(submission), evidence],
            timeProvider,
            new ReadinessWorkflowDependencies(
                new SimulatedTestEvidenceProvider(TestEvidence(submission)),
                new TestReadinessPolicy(),
                new SimulatedSecurityEvidenceProvider(SecurityEvidence(submission)),
                new SecurityReadinessPolicy(),
                provider,
                policy));
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static async Task<ReadinessWorkflowTestHost> CreateAsync(
        ReleaseSubmission submission,
        IReadOnlyCollection<EvidenceRecord> initialEvidence,
        TimeProvider timeProvider,
        ReadinessWorkflowDependencies dependencies)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var dataService = new ApplicationDataService(context);
        await dataService.SubmitReleaseAsync(
            submission,
            initialEvidence,
            new TimelineEntry(
                Guid.NewGuid(),
                submission.ReleaseId,
                1,
                TimelineEntryKind.ReleaseSubmitted,
                "Release submitted.",
                submission.SubmittedAt),
            $"workflow-contract:{submission.ReleaseId.Value}:submit");
        var input = new EvaluationRoundStart(
            Guid.NewGuid(),
            1,
            new UtcInstant(timeProvider.GetUtcNow()),
            []);
        return new ReadinessWorkflowTestHost(
            connection,
            context,
            submission,
            dataService,
            timeProvider,
            dependencies,
            input);
    }

    private static ITestEvidenceProvider TestProvider(
        TestEvidenceRecord evidence,
        BranchOutcome outcome) => outcome switch
        {
            BranchOutcome.MissingEvidence => new SimulatedTestEvidenceProvider(null),
            BranchOutcome.TransientFailure => new SimulatedTestEvidenceProvider(
                evidence,
                knownTransientFailuresBeforeSuccess: 3),
            BranchOutcome.Blocked or BranchOutcome.Passed => new SimulatedTestEvidenceProvider(evidence),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown Test outcome."),
        };

    private static ISecurityEvidenceProvider SecurityProvider(
        SecurityEvidenceRecord evidence,
        BranchOutcome outcome) => outcome switch
        {
            BranchOutcome.MissingEvidence => new SimulatedSecurityEvidenceProvider(null),
            BranchOutcome.TransientFailure => new SimulatedSecurityEvidenceProvider(
                evidence,
                knownTransientFailuresBeforeSuccess: 3),
            BranchOutcome.Blocked or BranchOutcome.Passed => new SimulatedSecurityEvidenceProvider(evidence),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown Security outcome."),
        };

    private static IChangeEvidenceProvider ChangeProvider(
        ChangeEvidenceRecord evidence,
        BranchOutcome outcome) => outcome switch
        {
            BranchOutcome.MissingEvidence => new SimulatedChangeEvidenceProvider(null),
            BranchOutcome.TransientFailure => new SimulatedChangeEvidenceProvider(
                evidence,
                knownTransientFailuresBeforeSuccess: 3),
            BranchOutcome.Blocked or BranchOutcome.Passed => new SimulatedChangeEvidenceProvider(evidence),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown Change outcome."),
        };

    private static TestEvidenceRecord TestEvidence(
        ReleaseSubmission submission,
        bool blocked = false) => new(
        TestEvidenceId,
        submission.ReleaseId,
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
        submission.ReleaseId,
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
        submission.ReleaseId,
        1,
        submission.SubmittedAt,
        null,
        isApproved: approved,
        submission.RequestedDeploymentWindow);

    private static ReleaseSubmission ContractSubmission() => new(
        new ReleaseId("workflow-contract"),
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
