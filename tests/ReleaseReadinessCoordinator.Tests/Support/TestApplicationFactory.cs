using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Support;

internal sealed class TestApplicationFactory : IAsyncDisposable
{
    private readonly ScenarioBuilder _scenario;
    private readonly AppDbContext _context;
    private readonly ApplicationDataService _dataService;
    private readonly CheckpointStoreCoordinator _coordinator;
    private readonly ReleaseWorkflowService _workflowService;
    private readonly bool _ownsCoordinator;

    public TestApplicationFactory(ScenarioBuilder scenario, AppDbContext context)
        : this(
            scenario,
            context,
            new CheckpointStoreCoordinator(scenario.CheckpointDirectory),
            ownsCoordinator: true)
    {
    }

    private TestApplicationFactory(
        ScenarioBuilder scenario,
        AppDbContext context,
        CheckpointStoreCoordinator coordinator,
        bool ownsCoordinator)
    {
        _scenario = scenario;
        _context = context;
        _dataService = new ApplicationDataService(context);
        _coordinator = coordinator;
        _ownsCoordinator = ownsCoordinator;
        _workflowService = new ReleaseWorkflowService(
            _dataService,
            _coordinator,
            scenario.Clock,
            scenario.Calls.CreateDependencies);
    }

    public TestApplicationFactory CreatePeer() => new(
        _scenario,
        _scenario.CreateContext(),
        _coordinator,
        ownsCoordinator: false);

    public async Task<PendingWorkflowWait> StartAsync()
    {
        await _workflowService.StartAsync(
            _scenario.Submission,
            new EvaluationRoundStart(Guid.NewGuid(), 1, _scenario.Now, []),
            _scenario.SessionId);
        return await _workflowService.RestoreAsync(_scenario.Submission.ReleaseId);
    }

    public Task<PendingWorkflowWait> RestoreAsync() =>
        _workflowService.RestoreAsync(_scenario.Submission.ReleaseId);

    public Task ResumeRemediationAsync(RemediationWorkflowResponse response) =>
        _workflowService.ResumeRemediationAsync(_scenario.Submission.ReleaseId, response);

    public Task ResumeApprovalAsync(ApprovalResponse response) =>
        _workflowService.ResumeApprovalAsync(_scenario.Submission.ReleaseId, response);

    public Task<RoundAggregation> ReplayAggregationAsync(EvaluationRound round) =>
        new RoundAggregator(_dataService, _scenario.Clock).CompleteAsync(
            new EvaluationRoundStart(round.Id, round.RoundNumber, round.StartedAt, []),
            round.Results);

    public Task<bool> SubmitRemediationAsync(
        string correlationToken,
        IReadOnlyCollection<EvidenceRecord> evidenceReplacements,
        IReadOnlyCollection<ReadinessCheck> explicitlySelectedChecks) =>
        new RemediationInteractionService(_dataService, _workflowService, _scenario.Clock)
            .SubmitAsync(
                _scenario.Submission.ReleaseId,
                correlationToken,
                evidenceReplacements,
                explicitlySelectedChecks);

    public Task<bool> SubmitDecisionAsync(
        Guid responseId,
        HumanDecision decision,
        string responder,
        string comment) =>
        new DecisionInteractionService(_dataService, _workflowService, _scenario.Clock)
            .SubmitAsync(
                _scenario.Submission.ReleaseId,
                responseId,
                decision,
                responder,
                comment);

    public async Task<ReleaseDetailProjection> GetDetailAsync() =>
        await _dataService.GetReleaseDetailAsync(_scenario.Submission.ReleaseId)
        ?? throw new InvalidOperationException("The scenario release does not exist.");

    public async ValueTask DisposeAsync()
    {
        if (_ownsCoordinator)
        {
            _coordinator.Dispose();
        }

        await _context.DisposeAsync();
    }
}
