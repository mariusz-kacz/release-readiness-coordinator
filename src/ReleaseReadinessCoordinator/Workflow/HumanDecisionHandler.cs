using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Workflow;

internal sealed class HumanDecisionHandler(
    ReleaseRevisionKey releaseRevision,
    IApplicationDataService dataService)
{
    private readonly ReleaseRevisionKey _releaseRevision =
        releaseRevision ?? throw new ArgumentNullException(nameof(releaseRevision));
    private readonly IApplicationDataService _dataService =
        dataService ?? throw new ArgumentNullException(nameof(dataService));

    public async Task<PersistedHumanResponse> HandleAsync(
        HumanResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var detail = await _dataService.GetReleaseDetailAsync(_releaseRevision, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release revision '{_releaseRevision.ReleaseId}/{_releaseRevision.Revision}' does not exist.");

        if (detail.TerminalResponse?.Response.Id == response.Id)
        {
            if (detail.TerminalResponse.Response != response)
            {
                throw new InvalidOperationException(
                    $"Human response ID '{response.Id}' was already used with different content.");
            }

            return detail.TerminalResponse;
        }

        if (detail.Release.Phase is not ProcessPhase.WaitingForApproval)
        {
            throw new ApplicationDataConflictException(
                ApplicationDataConflictKind.InvalidState,
                "A human response requires a release waiting for approval.");
        }

        var activeRequest = detail.HumanDecisionRequests
            .OrderBy(request => request.CreatedAt)
            .LastOrDefault()
            ?? throw new InvalidOperationException("A human response requires a durable approval request.");
        var nextSequence = detail.Timeline.IsEmpty ? 1 : detail.Timeline[^1].Sequence + 1;
        return await _dataService.SaveHumanResponseAsync(
            _releaseRevision,
            activeRequest.Id,
            response,
            new TimelineEntry(
                Guid.NewGuid(),
                _releaseRevision,
                nextSequence,
                TimelineEntryKind.HumanResponseAccepted,
                $"Human response '{response.Decision}' accepted from '{response.Responder}'.",
                response.RespondedAt),
            RoundAggregator.OperationKey(_releaseRevision, $"human-response:{response.Id:N}"),
            cancellationToken);
    }
}
