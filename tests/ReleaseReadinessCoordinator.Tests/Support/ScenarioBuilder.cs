using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Support;

internal sealed class ScenarioBuilder : IAsyncDisposable
{
    private readonly string _rootPath;
    private readonly string _databasePath;

    private ScenarioBuilder(
        string rootPath,
        string releaseId,
        BranchOutcome test,
        BranchOutcome security,
        BranchOutcome change)
    {
        _rootPath = rootPath;
        _databasePath = Path.Combine(rootPath, "release-readiness.db");
        CheckpointDirectory = Directory.CreateDirectory(Path.Combine(rootPath, "workflow-checkpoints"));
        Now = Utc(2026, 8, 20, 10);
        Clock = new MutableTimeProvider(Now.Value);
        Submission = new ReleaseSubmission(
            new ReleaseId(releaseId),
            "orders",
            "2.4.0",
            new UtcInterval(Now, Utc(2026, 8, 20, 11)),
            Utc(2026, 8, 20, 9));
        SessionId = $"workflow-{releaseId}";
        InitialOutcomes = new Dictionary<ReadinessCheck, BranchOutcome>
        {
            [ReadinessCheck.Test] = test,
            [ReadinessCheck.Security] = security,
            [ReadinessCheck.Change] = change,
        };
        Calls.Configure(test, security, change);
    }

    public DirectoryInfo CheckpointDirectory { get; }

    public CountingFakes Calls { get; } = new();

    public MutableTimeProvider Clock { get; }

    public IReadOnlyDictionary<ReadinessCheck, BranchOutcome> InitialOutcomes { get; }

    public UtcInstant Now { get; }

    public ReleaseSubmission Submission { get; }

    public string SessionId { get; }

    public static async Task<ScenarioBuilder> CreateAsync(
        string releaseId,
        BranchOutcome test = BranchOutcome.Passed,
        BranchOutcome security = BranchOutcome.Passed,
        BranchOutcome change = BranchOutcome.Passed)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "release-readiness-workflow-scenarios",
            Guid.NewGuid().ToString("N"));
        var scenario = new ScenarioBuilder(rootPath, releaseId, test, security, change);
        await using var context = scenario.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var dataService = new ApplicationDataService(context);
        await dataService.SubmitReleaseAsync(
            scenario.Submission,
            scenario.InitialEvidence(),
            new TimelineEntry(
                Guid.NewGuid(),
                scenario.Submission.ReleaseId,
                1,
                TimelineEntryKind.ReleaseSubmitted,
                "Release submitted.",
                scenario.Submission.SubmittedAt),
            $"scenario:{releaseId}:submit");
        return scenario;
    }

    public TestApplicationFactory CreateApplication() => new(this, CreateContext());

    public ApprovalResponse ApprovalResponse(HumanDecision decision) => new(new HumanResponse(
        Guid.NewGuid(),
        decision,
        "release-manager",
        "Reviewed the immutable decision brief.",
        new UtcInstant(Clock.GetUtcNow())));

    public async Task<RemediationWorkflowResponse> RemediationResponseAsync(
        PendingRemediationWait wait,
        IReadOnlyCollection<ReadinessCheck> replacementChecks,
        IReadOnlyCollection<ReadinessCheck>? explicitlySelectedChecks = null)
    {
        await using var context = CreateContext();
        var detail = await new ApplicationDataService(context).GetReleaseDetailAsync(Submission.ReleaseId)
            ?? throw new InvalidOperationException("The scenario release does not exist.");
        var replacements = replacementChecks.Select(check => PassingEvidence(check, detail)).ToArray();
        var submission = new RemediationSubmission(
            Guid.NewGuid(),
            wait.RemediationRequestId,
            new UtcInstant(Clock.GetUtcNow()),
            replacements.ToDictionary(evidence => evidence.Kind, evidence => evidence.Id),
            explicitlySelectedChecks ?? []);
        return new RemediationWorkflowResponse(submission, replacements);
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False;Default Timeout=30")
            .Options;
        return new AppDbContext(options);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private EvidenceRecord[] InitialEvidence() =>
        Enum.GetValues<ReadinessCheck>()
            .Where(check => InitialOutcomes[check] is not BranchOutcome.MissingEvidence)
            .Select(EvidenceFor)
            .ToArray();

    private EvidenceRecord EvidenceFor(ReadinessCheck check) => check switch
    {
        ReadinessCheck.Test => new TestEvidenceRecord(
            Guid.NewGuid(), Submission.ReleaseId, 1, Submission.SubmittedAt, null,
            Submission.ReleaseVersion, Submission.SubmittedAt,
            InitialOutcomes[check] is BranchOutcome.Blocked ? 0.90m : 0.99m,
            []),
        ReadinessCheck.Security => new SecurityEvidenceRecord(
            Guid.NewGuid(), Submission.ReleaseId, 1, Submission.SubmittedAt, null,
            Submission.ReleaseVersion, Submission.SubmittedAt,
            InitialOutcomes[check] is BranchOutcome.Blocked ? ["CRITICAL-1"] : [],
            [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
        ReadinessCheck.Change => new ChangeEvidenceRecord(
            Guid.NewGuid(), Submission.ReleaseId, 1, Submission.SubmittedAt, null,
            InitialOutcomes[check] is not BranchOutcome.Blocked,
            Submission.RequestedDeploymentWindow),
        _ => throw new ArgumentOutOfRangeException(nameof(check)),
    };

    private EvidenceRecord PassingEvidence(
        ReadinessCheck check,
        ReleaseDetailProjection detail)
    {
        var kind = check switch
        {
            ReadinessCheck.Test => EvidenceKind.Test,
            ReadinessCheck.Security => EvidenceKind.Security,
            ReadinessCheck.Change => EvidenceKind.Change,
            _ => throw new ArgumentOutOfRangeException(nameof(check)),
        };
        detail.CurrentEvidence.TryGetValue(kind, out var current);
        var recordedAt = new UtcInstant(Clock.GetUtcNow());
        var version = current?.Version + 1 ?? 1;
        return check switch
        {
            ReadinessCheck.Test => new TestEvidenceRecord(
                Guid.NewGuid(), Submission.ReleaseId, version, recordedAt, current?.Id,
                Submission.ReleaseVersion, recordedAt, 0.99m, []),
            ReadinessCheck.Security => new SecurityEvidenceRecord(
                Guid.NewGuid(), Submission.ReleaseId, version, recordedAt, current?.Id,
                Submission.ReleaseVersion, recordedAt, [], [],
                new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
            ReadinessCheck.Change => new ChangeEvidenceRecord(
                Guid.NewGuid(), Submission.ReleaseId, version, recordedAt, current?.Id,
                true, Submission.RequestedDeploymentWindow),
            _ => throw new ArgumentOutOfRangeException(nameof(check)),
        };
    }

    internal static UtcInstant Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));
}

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}
