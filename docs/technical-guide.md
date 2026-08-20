# Technical guide

This guide contains the setup, operation, verification, and architecture details for Release Readiness Coordinator. For a project overview, start with the [README](../README.md).

## Prerequisites

- .NET 10 SDK
- A writable local directory for SQLite, Microsoft Agent Framework (MAF) checkpoints, and ASP.NET Core data-protection keys

No database server, queue, worker, cloud account, or other service is required.

## Run locally

From the repository root:

```text
dotnet restore
dotnet build --no-restore
dotnet run --project src/ReleaseReadinessCoordinator/ReleaseReadinessCoordinator.csproj --launch-profile http
```

Open [http://localhost:5247](http://localhost:5247). The application creates its local stores on first start.

To keep a demonstration separate from the default data, set an absolute application-data directory before starting. For example, in PowerShell:

```powershell
$env:ApplicationData__Directory = Join-Path $PWD "artifacts/demo-data"
dotnet run --project src/ReleaseReadinessCoordinator/ReleaseReadinessCoordinator.csproj --launch-profile http
```

Use a new directory or a unique release ID when repeating a journey. Release IDs and submitted release metadata are immutable.

## Built-in fixtures

| Fixture | Intended use |
|---|---|
| **Complete evidence** | Starts with passing Test, Security, and Change facts. Edit individual fields to demonstrate deterministic blockers. |
| **No initial evidence** | Omits all three evidence records and demonstrates the single remediation wait created after complete fan-in. |

Loading a fixture only fills the form. You can review or edit its values before submitting.

## Demonstration journeys

### Passing release and human decision

1. On **Submit release**, select **Load Complete evidence**.
2. Change the release ID to a unique value and submit the release.
3. On the release detail page, confirm the phase is `WaitingForApproval`, all three current results are `Passed` and `Executed`, and the deterministic decision brief is present.
4. Select **Open decision form**.
5. Enter the reviewer and comment, then select **Approve release** or **Reject release**.
6. Confirm the terminal detail shows `Approved` or `Rejected`, the audited response, and no active workflow wait.

The response is terminal for the immutable point-in-time snapshot; the MVP does not route approval back through readiness evaluation.

### Blockers, remediation, and selective reuse

1. Select **Load Complete evidence** and choose a unique release ID.
2. Make Test block by setting its pass rate below 95%, for example `90`.
3. Make Security block by entering `CRITICAL-1` as an unresolved critical finding.
4. Leave Change approved with an approved window that contains the requested deployment window, then submit.
5. Confirm the detail page reaches `WaitingForRemediation` only after showing one result for each check: Test and Security blocked, Change passed.
6. Select **Open remediation form**, then **Load ready demo evidence**. This updates only the branches with active problems.
7. Select **Save evidence and run next evaluation**.
8. On round 2, confirm Test and Security are `Executed`; Change is `Reused` because its evidence record is unchanged, links to its source round, and has no execution attempts.
9. Complete the resulting human decision if desired.

The workflow advances during form requests. Use **Refresh status** when observing a page from another tab; the application intentionally has no live-update channel.

## Restart recovery demonstration

Use one stable `ApplicationData__Directory` for the entire demonstration. Never run multiple application instances against the same checkpoint directory.

1. Progress a release to either `WaitingForRemediation` or `WaitingForApproval`.
2. Stop the host with Ctrl+C. Do not delete or edit the application-data directory.
3. Restart with the same command, configuration, and application-data directory.
4. On the home page, enter the release ID under **Continue existing workflow**.
5. Open the active remediation or decision form. The application uses the SQLite session ID to restore the MAF runtime from the latest checkpoint, reconciles the pending typed request with SQLite, and renders the durable SQLite business data.
6. Submit the response once and confirm the workflow continues or terminates without duplicate rounds, requests, timeline entries, or human responses.

Repeat the procedure at both wait types to demonstrate both recovery paths. A change to workflow topology, stable executor or port identity, or checkpoint contents can make continuation incompatible. The application fails closed and displays that condition as a workflow failure.

## Application data

By default, state is written below `src/ReleaseReadinessCoordinator/app-data/`:

| Path | Purpose |
|---|---|
| `release-readiness.db` | SQLite business records and append-only audit history |
| `workflow-checkpoints/` | MAF continuation state and pending external requests |
| `data-protection-keys/` | ASP.NET Core data-protection keys used by server-rendered forms |

The whole `app-data/` tree is ignored by Git. `ApplicationData__Directory` overrides the root and may be absolute or relative to the web project's content root.

Local portfolio state is disposable and has no migration compatibility guarantee. After pulling a persisted-shape change, stop the application and delete both `release-readiness.db` (including any `-shm` or `-wal` sidecars) and the matching `workflow-checkpoints/` directory before restarting. Never retain one without the other because their release sessions and workflow continuations are correlated.

Persisted instants remain UTC. Visible and editable date/time values use the application host's local timezone and omit timezone text.

### Checkpoint trust boundary

Checkpoint files are trusted, private MAF runtime infrastructure. MAF deserializes and restores complete workflow state before application-level reconciliation, so arbitrary or tampered checkpoint files are not safe input. The application's pending-request payload contains only a typed durable request reference. SQLite supplies the session ID used to locate the checkpoint, the expected wait identity, and all displayed and persisted business content.

Exact identity reconciliation prevents checkpoint state from replacing SQLite domain facts; it does not make an untrusted checkpoint safe to load. Restrict checkpoint-directory access to the application identity in any hosted environment.

The filesystem checkpoint store is process-exclusive and not thread-safe. The application protects it with one application-wide asynchronous gate, and multiple processes must never share one checkpoint directory. SQLite and checkpoint writes are not one atomic transaction, so stable operation keys, idempotent writes, and exact identity reconciliation bound the recovery gap.

This is a single-machine demo design, not a horizontally scalable or highly available deployment. See [ADR-001](decisions/ADR-001-use-process-exclusive-filesystem-checkpointing.md) for the store and concurrency decision and [ADR-002](decisions/ADR-002-keep-domain-content-authoritative-in-sqlite.md) for the checkpoint and SQLite trust boundary.

## Architecture

For a deeper explanation of module boundaries, runtime flow, workflow topology, data relationships, recovery, and testing structure, see the [architecture document](architecture.md).

- `src/ReleaseReadinessCoordinator/` is the only deployable application.
- `tests/ReleaseReadinessCoordinator.Tests/` is the only test project.
- SQLite is accessed through one bounded application data service; there are no generic repositories or CQRS or event-sourcing layers.
- MAF owns workflow execution and typed external waits; deterministic C# owns readiness policy and selective-reuse decisions.
- Release metadata and evidence history are immutable. Remediation appends new evidence versions; it does not edit the release.
- An HTTP request starts or resumes a workflow and runs it until the next external request or terminal result.
- The application deliberately has no separate client, service, worker, queue, scheduler, broker, event bus, or real-time channel.

The four Razor routes are:

| Route | Purpose |
|---|---|
| `/` | Submit a release or continue an existing workflow |
| `/Releases/{releaseId}` | Inspect release state, results, evidence, history, and timeline |
| `/Releases/{releaseId}/Remediate` | Supply evidence and select explicit reruns for the active remediation request |
| `/Releases/{releaseId}/Decision` | Review the immutable snapshot and approve or reject the release |

The complete requirements are in [SPEC.md](../SPEC.md). The [acceptance matrix](acceptance-matrix.md) maps all 13 MVP criteria to executable or manual evidence.

## Commands

| Command | Purpose |
|---|---|
| `dotnet restore` | Restore pinned dependencies |
| `dotnet build --no-restore` | Build the application and focused test project |
| `dotnet test --no-build` | Run the complete automated suite |
| `dotnet test --no-build --filter "FullyQualifiedName~WorkflowScenario"` | Run the real-graph orchestration scenarios |
| `dotnet test --no-build --filter "FullyQualifiedName~ReleaseReadinessCoordinator.Tests.Web"` | Run Razor and Kestrel UI integration tests |
| `dotnet test --no-build --filter "FullyQualifiedName~RestartRecovery|FullyQualifiedName~CheckpointContract"` | Run recovery and checkpoint contract tests |
| `dotnet format --verify-no-changes` | Verify repository formatting |

UI verification uses focused Razor and Kestrel integration tests plus the two presenter walkthroughs above. The test project intentionally has no browser-automation runtime.
