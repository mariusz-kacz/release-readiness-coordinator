# Implementation Tasks: Release Readiness Coordinator MVP

This checklist implements `SPEC.md` without changing its authority. Complete tasks in dependency order and stop at each checkpoint for review. Commands assume repository root.

## Task 1: Bootstrap the .NET 10 solution and command baseline

**Description:** Create one solution, one deployable Razor Pages application, and one focused xUnit test project. Pin the justified package references, enable nullable analysis, exclude application data from source control, and record the root commands in a minimal `AGENTS.md`.

**Acceptance criteria:**

- [x] `ReleaseReadinessCoordinator.slnx` contains exactly the web and test projects, both targeting .NET 10.
- [x] The web project directly pins `Microsoft.Agents.AI.Workflows` to `1.17.0` and includes only spec-justified EF Core/SQLite dependencies.
- [x] `AGENTS.md` records the four required root commands and the one-app architecture boundary.

**Verification:**

- [x] `dotnet restore`
- [x] `dotnet build --no-restore`
- [x] `dotnet test --no-build`
- [x] `dotnet format --verify-no-changes`

**Dependencies:** None

**Files likely touched:**

- `ReleaseReadinessCoordinator.slnx`
- `src/ReleaseReadinessCoordinator/ReleaseReadinessCoordinator.csproj`
- `src/ReleaseReadinessCoordinator/Program.cs`
- `tests/ReleaseReadinessCoordinator.Tests/ReleaseReadinessCoordinator.Tests.csproj`
- `AGENTS.md`

**Estimated scope:** Medium (5 primary files)

## Task 2: Prove the fixed MAF three-branch graph

**Description:** Build the smallest production-shaped static MAF graph with stable executor IDs, typed planner/work/result messages, and one explicit conditional edge per readiness branch. Prove that one planner invocation routes work to three branch executors in one evaluation round and the aggregator runs only after all three results exist.

**Acceptance criteria:**

- [x] The planner emits exactly one Test, Security, and Change `BranchWorkItem` with an Execute/Reuse disposition.
- [x] Three stable-ID executors each emit exactly one typed `BranchResult`; the aggregator rejects duplicates, omissions, and impossible branch identities as technical failures.
- [x] A real MAF 1.17.0 test demonstrates complete three-source fan-in and no pre-fan-in external request.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~WorkflowTopology"`
- [x] `dotnet build --no-restore`
- [x] Inspect emitted executor events to confirm all three branch IDs precede aggregation.

**Dependencies:** Task 1

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/WorkflowMessages.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReleaseWorkflowFactory.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReadinessExecutors.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/WorkflowTopologyTests.cs`

**Estimated scope:** Medium (4 files)

## Task 3: Prove typed waits and checkpoint rehydration

**Description:** Extend the static graph with typed remediation and approval `RequestPort`s and a filesystem-backed checkpoint manager. Verify a rebuilt graph with identical topology and IDs re-emits the same pending request after a simulated process boundary and accepts only its correlated response.

**Acceptance criteria:**

- [x] Remediation and approval have distinct typed request/response contracts and occur only after fan-in.
- [x] A pending request, including identity and type, is recovered from `FileSystemJsonCheckpointStore` after disposing the first run and rebuilding the graph.
- [x] Missing, corrupt, mismatched, or incompatible continuation state produces a visible technical failure and never starts a new workflow.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~CheckpointContract"`
- [x] `dotnet test --no-build --filter "FullyQualifiedName~ExternalRequestContract"`
- [x] Manually inspect the temporary checkpoint directory and restored request correlation.

**Dependencies:** Task 2

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/WorkflowMessages.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReleaseWorkflowFactory.cs`
- `src/ReleaseReadinessCoordinator/Workflow/CheckpointStoreCoordinator.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/CheckpointContractTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint A: Framework viability

- [x] Tasks 1-3 acceptance criteria are met.
- [x] All root commands pass.
- [x] MAF 1.17.0 topology, external-request, rehydration, and stable-ID assumptions are proven in executable tests.
- [ ] Human review approves continuation without changing the pinned version or graph strategy.

## Task 4: Model immutable releases and versioned evidence

**Description:** Define the release revision, UTC value objects, immutable submission metadata, lifecycle phases, and three typed immutable/versioned evidence records used by the readiness workflow.

**Acceptance criteria:**

- [x] Release identifiers, revisions, deployment windows, and timestamps are validated; Approved and Rejected revisions cannot reopen.
- [x] Test, Security, and Change evidence records have stable IDs, positive versions, optional supersession links, and immutable branch-specific facts.
- [x] Change evidence contains approval state and approved window, release metadata is immutable within a revision, and all domain timestamps are supplied UTC instants.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~ReleaseAndEvidenceContract"`
- [x] `dotnet test --no-build --filter "FullyQualifiedName~DomainInvariant"`
- [x] `dotnet build --no-restore`

**Dependencies:** Task 3

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Domain/Releases.cs`
- `src/ReleaseReadinessCoordinator/Domain/Evidence.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Domain/ReleaseAndEvidenceContractTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Domain/DomainInvariantTests.cs`

**Estimated scope:** Medium (4 files)

## Task 5: Define evaluation and selective-reuse contracts

**Description:** Model branch work, outcomes, execution/reuse disposition, planning reasons, immutable results, complete evaluation rounds, and remediation requests without implementing the round planner itself.

**Acceptance criteria:**

- [x] Readiness check, branch outcome, work disposition, execution disposition, and planning reason remain separate bounded concepts.
- [x] Work items enforce valid Execute/Reuse reason combinations; results record exact evidence identity, optional deadline, findings, attempts, and valid reuse-source linkage.
- [x] Passing results require evidence and a deadline, non-passing results have no deadline, and completed rounds contain exactly one result for each readiness check.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~EvaluationContract"`
- [x] `dotnet test --no-build --filter "FullyQualifiedName~DomainInvariant"`
- [x] `dotnet build --no-restore`

**Dependencies:** Task 4

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Domain/Evaluations.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Domain/EvaluationContractTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Domain/DomainInvariantTests.cs`

**Estimated scope:** Medium (3 files)

## Task 6: Define decision, correlation, and audit contracts

**Description:** Model immutable decision snapshots, typed human requests/responses, workflow correlation, and chronological timeline records for durable business history.

**Acceptance criteria:**

- [x] A decision snapshot contains exactly three passing source results, their evidence and evaluator identities, the earliest deadline, and one immutable deterministic brief.
- [x] Human requests/responses preserve stable identities, terminal decision intent, actor, and UTC timestamps while keeping workflow correlation distinct from business history.
- [x] Correlation and timeline records preserve stable identities, positive ordering, UTC timestamps, and distinct business/audit meanings.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~DecisionContract"`
- [x] `dotnet test --no-build --filter "FullyQualifiedName~DomainInvariant"`
- [x] `dotnet build --no-restore`

**Dependencies:** Task 5

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Domain/Decisions.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Domain/DecisionContractTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Domain/DomainInvariantTests.cs`

**Estimated scope:** Medium (3 files)

## Task 7: Establish evidence-identity and freshness rules

**Description:** Define exact evidence-record identity as the branch change-detection contract and provide deterministic deadline calculation for time-limited Test and Security evidence.

**Acceptance criteria:**

- [x] A new evidence version has a new reuse identity even when its business facts equal the prior record, and only its matching branch identity changes.
- [x] Test and Security deadlines use the 24-hour maximum with earlier evidence-specific bounds honored; boundary checks use an injected `TimeProvider`.
- [x] Domain change detection uses evidence IDs, explicit selection, and `ValidUntil`; it has no fingerprint, input-generation, invalidation-map, or stored-validity-state contract.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~EvidenceIdentity"`
- [x] `dotnet test --no-build --filter "FullyQualifiedName~Freshness"`
- [x] `rg -n "Fingerprint|ResultInvalidation|ResultValidity|InputGeneration" src` returns no matches.
- [x] `dotnet build --no-restore`

**Dependencies:** Task 5

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Domain/FreshnessDeadlines.cs`
- `src/ReleaseReadinessCoordinator/Domain/Evaluations.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Domain/EvidenceIdentityAndFreshnessTests.cs`

**Estimated scope:** Medium (3 files)

## Checkpoint B1: Domain semantics

- [x] Tasks 4-7 acceptance criteria are met.
- [x] Immutable releases/evidence, evaluation, decision, correlation, and audit contracts match the current specification.
- [x] State dimensions remain separate, while change and freshness are represented by evidence ID and `ValidUntil`.

## Task 8: Create the SQLite schema and migrations

**Description:** Map business/audit state to SQLite using EF Core. Use unique constraints for release revision identity, operation idempotency, one-result-per-branch-per-round, active requests, immutable snapshots, and terminal decisions.

**Acceptance criteria:**

- [x] The schema represents all stores named in `SPEC.md` section 12.3, enforces immutable release metadata and at most one current evidence record per branch, and preserves versioned evidence plus append-only history.
- [x] Unique indexes enforce duplicate submission conflicts, stable operation keys, and one branch result per check/round.
- [x] A migration creates a fresh database and database constraints protect active request/snapshot mutation points.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~DatabaseSchema"`
- [x] Apply the migration to a temporary SQLite file and inspect expected tables/indexes.
- [x] `dotnet build --no-restore`

**Dependencies:** Tasks 4-7

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Data/AppDbContext.cs`
- `src/ReleaseReadinessCoordinator/Data/EntityConfiguration.cs`
- `src/ReleaseReadinessCoordinator/Data/Migrations/InitialCreate.cs`
- `src/ReleaseReadinessCoordinator/Data/Migrations/AppDbContextModelSnapshot.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Data/DatabaseSchemaTests.cs`

**Estimated scope:** Medium (5 primary files; generated migration metadata is mechanical)

## Task 9: Implement the bounded idempotent application data service

**Description:** Provide small, use-case-oriented async operations over `AppDbContext` for submissions, evidence, rounds/results, requests, snapshots/responses, correlations, failures, and timeline entries. Make workflow replay harmless through stable operation keys and transactions local to SQLite.

**Acceptance criteria:**

- [x] Replaying the same operation key returns the existing business record without duplicate rows or timeline entries.
- [x] Duplicate release identifier/revision returns conflict, and Approved/Rejected revisions cannot reopen.
- [x] Reads reconstruct a coherent release-detail projection, while an evidence replacement atomically persists the new version, links the superseded record, and changes the matching branch's current-evidence selection; no operation edits release metadata.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~ApplicationDataService"`
- [x] A restart-style replay test submits the same operation twice and verifies one durable effect.
- [x] `dotnet format --verify-no-changes`

**Dependencies:** Task 8

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Data/IApplicationDataService.cs`
- `src/ReleaseReadinessCoordinator/Data/ApplicationDataService.cs`
- `src/ReleaseReadinessCoordinator/Data/ReleaseDetailProjection.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Data/ApplicationDataServiceTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint B2: Durable foundation

- [x] Tasks 4-9 acceptance criteria are met.
- [x] Domain and data tests pass against temporary SQLite databases.
- [x] State dimensions, evidence-identity rules, deadlines, immutability, constraints, and replay safety match the specification.
- [x] No generic repository, CQRS, event-sourcing, or speculative layer exists.

## Task 10: Deliver release submission and demo fixtures

**Description:** Implement the first vertical web slice: a coordinator submits one release revision with compact evidence fields or a chosen local demo fixture, the application persists it, starts the workflow, and redirects to release detail.

**Acceptance criteria:**

- [x] The form captures every release submission field and initial evidence category required by `SPEC.md` section 5.
- [x] Valid submission creates immutable release metadata and up to three initial evidence records, then starts the correlated workflow; omitted evidence remains representable as `MissingEvidence`.
- [x] Duplicate identifier/revision produces HTTP 409 semantics and a useful page message; terminal revisions are not reopened.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~ReleaseSubmission"`
- [x] `dotnet run --project src/ReleaseReadinessCoordinator` and manually submit a demo fixture.
- [x] Confirm POST/redirect/GET and one persisted release revision.

**Dependencies:** Tasks 3 and 9

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Pages/Releases/New.cshtml`
- `src/ReleaseReadinessCoordinator/Pages/Releases/New.cshtml.cs`
- `src/ReleaseReadinessCoordinator/Simulation/DemoReleaseFixtures.cs`
- `src/ReleaseReadinessCoordinator/Program.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Web/ReleaseSubmissionTests.cs`

**Estimated scope:** Medium (5 files)

## Task 11: Deliver the Test readiness slice

**Description:** Add the simulated Test evidence provider, typed transient failure classification/retry harness, deterministic Test policy, and branch executor integration.

**Acceptance criteria:**

- [x] Missing required facts map to `MissingEvidence`; version, pass-rate, critical-suite, or freshness misses map to `Blocked`; otherwise the result is `Passed`.
- [x] Only typed known provider failures retry, with exactly three total immediate attempts; exhaustion returns `TransientFailure` with attempt details.
- [x] Boundary tests cover 95%, exact version match, critical failures, and the validity deadline.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~TestReadiness"`
- [x] Counting fakes verify provider and policy call counts.
- [x] `dotnet build --no-restore`

**Dependencies:** Tasks 5 and 7

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Readiness/BranchExecution.cs`
- `src/ReleaseReadinessCoordinator/Readiness/TestEvidenceProvider.cs`
- `src/ReleaseReadinessCoordinator/Readiness/TestPolicy.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Readiness/TestReadinessTests.cs`

**Estimated scope:** Medium (4 files)

## Task 12: Deliver the Security readiness slice

**Description:** Add simulated Security evidence retrieval and deterministic evaluation of version, critical/high findings, scoped exceptions, expiry through the release window, and evidence freshness.

**Acceptance criteria:**

- [x] Missing scan/required exception facts, deterministic blockers, exhausted known transient failures, and passing evidence map to the exact four outcomes.
- [x] Every high finding requires a matching in-scope exception valid through the entire requested release window; unresolved critical findings always block.
- [x] Boundary tests cover exception scope/expiry, release-window changes, exact version match, and freshness.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~SecurityReadiness"`
- [x] Counting fakes prove no retry for missing evidence or deterministic blockers.
- [x] `dotnet format --verify-no-changes`

**Dependencies:** Tasks 7 and 11

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Readiness/SecurityEvidenceProvider.cs`
- `src/ReleaseReadinessCoordinator/Readiness/SecurityPolicy.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReadinessExecutors.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Readiness/SecurityReadinessTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint C1: Submission and first policies

- [x] Tasks 10-12 acceptance criteria are met.
- [x] A release can be submitted once and starts a correlated workflow.
- [x] Test and Security boundary, retry, and outcome-mapping tests pass.

## Task 13: Deliver the Change readiness slice

**Description:** Apply deterministic C# policy to approval and approved window from the current Change evidence record.

**Acceptance criteria:**

- [x] Missing approval or approved window maps to `MissingEvidence`; unapproved or out-of-window evidence maps to `Blocked`.
- [x] `Passed` requires change approval and full containment in the approved window, and uses the approved-window end as `ValidUntil`; non-passing results have no reuse deadline.
- [x] Known source retry exhaustion maps to `TransientFailure`; invariant and programming failures remain technical failures.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~ChangeReadiness"`
- [x] Data-driven tests cover window boundaries, missing approval, and missing approved window.
- [x] `dotnet build --no-restore`

**Dependencies:** Tasks 7 and 11

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Readiness/ChangeEvidenceProvider.cs`
- `src/ReleaseReadinessCoordinator/Readiness/ChangePolicy.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReadinessExecutors.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Readiness/ChangeReadinessTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint C2: Deterministic readiness

- [x] Tasks 10-13 acceptance criteria are met.
- [x] Data-driven policy and retry tests pass.
- [x] All four expected outcomes are representable without hiding technical failures.
- [x] The deterministic Change policy matches the approved evidence and window rules.

## Task 14: Implement selective execution and safe reuse planning

**Description:** Build the round planner that always emits three work items and chooses Execute/Reuse from prior outcome, current evidence identity, deadline, and explicit branch selection. Implement a separate defensive reuse path.

**Acceptance criteria:**

- [x] Every reuse prerequisite and overlapping-condition case produces the correct disposition and the specification's single prioritized planning reason; additional detail remains explanatory text rather than new domain reason types.
- [x] Reuse verifies exact evidence ID and deadline, emits a new result linked to source result/round, and explains why reuse is safe.
- [x] Reused work makes zero provider or policy calls; failed defensive verification becomes a technical failure rather than silent execution.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~SelectiveRerun"`
- [x] Counting fakes prove zero forbidden calls on reuse and expected calls on execution.
- [x] Evidence- and time-driven tests replace or expire one branch without executing unrelated branches.

**Dependencies:** Tasks 7 and 11-13

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/RoundPlanner.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ResultReuse.cs`
- `src/ReleaseReadinessCoordinator/Workflow/WorkflowMessages.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/SelectiveRerunTests.cs`

**Estimated scope:** Medium (4 files)

## Task 15: Complete aggregation and remediation resumption

**Description:** Connect planner and branch slices through the real fixed graph. Persist complete rounds, create one remediation request containing all current problems after fan-in, accept correlated new evidence versions and explicit branch selections, and start the next selective round.

**Acceptance criteria:**

- [x] Each round persists exactly three results and complete Executed/Reused explanations before routing.
- [x] Any non-pass combination creates exactly one remediation request containing every current problem; no branch waits independently.
- [x] A current correlated remediation response creates only new evidence versions, atomically changes their matching current-evidence selections, records explicit branch selections without changing evidence, closes the request once, and resumes selective evaluation; release metadata cannot be edited.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~RemediationWorkflow"`
- [x] Real-graph test blocks multiple branches, remediates them, and proves unaffected branches reuse.
- [x] Duplicate or mismatched remediation responses have no duplicate effect.

**Dependencies:** Tasks 9 and 14

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/ReleaseWorkflowFactory.cs`
- `src/ReleaseReadinessCoordinator/Workflow/RoundAggregator.cs`
- `src/ReleaseReadinessCoordinator/Workflow/RemediationHandler.cs`
- `src/ReleaseReadinessCoordinator/Data/ApplicationDataService.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/RemediationWorkflowTests.cs`

**Estimated scope:** Medium (5 files)

## Task 16: Build immutable snapshots and terminal MAF human decisions

**Description:** For an all-pass round, persist one immutable `DecisionSnapshot` and deterministic brief, issue a typed MAF approval request, and persist one idempotent terminal approval/rejection delivered through the restored request. Simplify the prior decision contract by removing submitted snapshot/concurrency identities, stale-response persistence, freshness revalidation, and the approval-to-planner route.

**Acceptance criteria:**

- [x] Snapshot resolves three source results and their evidence IDs, earliest deadline, and immutable deterministic brief; the brief is never regenerated or hashed when the response arrives.
- [x] While approval is pending, evidence/remediation/rerun changes are disallowed. The restored MAF request ID and response type are the continuation authority; invalid continuation is rejected before workflow resumption and creates no human-response record.
- [x] A correctly resumed Approve/Reject response persists exactly one terminal decision, exact replay is harmless, conflicting response-ID reuse fails, and human decision has no edge back to the planner.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~DecisionIntegrity"`
- [x] Tests cover terminal approval, terminal rejection, exact replay, conflicting response-ID reuse, invalid external-request continuation with no business effect, and absence of an approval-to-planner route.
- [x] Brief output is byte-stable for identical snapshot inputs.

**Dependencies:** Task 15

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/DecisionSnapshotBuilder.cs`
- `src/ReleaseReadinessCoordinator/Workflow/HumanDecisionHandler.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReleaseWorkflowFactory.cs`
- `src/ReleaseReadinessCoordinator/Workflow/CheckpointStoreCoordinator.cs`
- `src/ReleaseReadinessCoordinator/Domain/Decisions.cs`
- `src/ReleaseReadinessCoordinator/Data/`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/DecisionIntegrityTests.cs`

**Estimated scope:** Large contract simplification; implement incrementally across domain/data, workflow, then tests while keeping Task 16 as the single owner of the correction.

## Task 16A: Allocate round and result identities once

**Description:** Clarify evaluation identity ownership without changing workflow behavior. Rename the start-message identity to `RoundId`, allocate one `ResultId` per planned branch, and use that same result identity for both executed and reused branch results instead of generating and then ignoring an extra ID.

**Acceptance criteria:**

- [x] `EvaluationRoundStart.RoundId` is the identity persisted as `EvaluationRound.Id`; no second round identity is created during aggregation.
- [x] Each `PlannedBranchWorkItem.ResultId` becomes the emitted `BranchResult.Id` for both Execute and Reuse paths.
- [x] Existing round ordering, branch outcomes, reuse linkage, provider calls, and persistence behavior remain unchanged.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~WorkflowTopology|FullyQualifiedName~SelectiveRerun|FullyQualifiedName~DecisionIntegrity"`
- [x] A focused test asserts planned round/result identities survive execution and reuse unchanged.
- [x] `dotnet build --no-restore`

**Dependencies:** Task 16

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/WorkflowMessages.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReadinessExecutors.cs`
- `src/ReleaseReadinessCoordinator/Readiness/BranchExecution.cs`
- `src/ReleaseReadinessCoordinator/Workflow/RoundAggregator.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/WorkflowTopologyTests.cs`

**Estimated scope:** Medium (5 files)

## Task 16B: Separate workflow waits from business requests

**Description:** Make the MAF continuation boundary explicit in names. Rename pending workflow types from `Pending*Request` to `Pending*Wait`, rename their engine-generated identifier to `WorkflowRequestId`, and rename remediation's optional domain correlation to `RemediationRequestId`. Keep durable `HumanDecisionRequest` and `RemediationRequest` entities unchanged.

**Acceptance criteria:**

- [x] MAF-generated identities are named `WorkflowRequestId` everywhere they are captured, restored, compared, or persisted.
- [x] Pending wait types cannot be mistaken for durable domain request records, and remediation correlation explicitly distinguishes both IDs.
- [x] Checkpoint restoration, mismatch rejection, response typing, and no-business-effect-on-invalid-continuation behavior remain unchanged.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~CheckpointContract|FullyQualifiedName~ExternalRequestContract|FullyQualifiedName~DecisionIntegrity"`
- [x] Search confirms no `PendingWorkflowRequest.RequestId`, `DomainRequestId`, or `Pending*Request` workflow type remains.
- [x] `dotnet build --no-restore`

**Dependencies:** Task 16A

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/CheckpointStoreCoordinator.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReleaseSubmissionApplicationService.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/CheckpointContractTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/ExternalRequestContractTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/DecisionIntegrityTests.cs`

**Estimated scope:** Medium (5 files)

## Task 16C: Clarify persisted approval-response references

**Description:** Rename the persisted response's `ActiveRequestId` reference to `ApprovalRequestId` across the domain projection, data-service boundary, and storage mapping. Preserve `HumanResponse.Id` as the response's own idempotency identity and preserve the one-terminal-response-per-release constraint.

**Acceptance criteria:**

- [ ] `PersistedHumanResponse.ApprovalRequestId` unambiguously identifies the durable `HumanDecisionRequest.Id` that was answered.
- [ ] Data-service parameters, persistence rows, constraints, and error messages consistently use approval-request terminology without changing cardinality or response semantics.
- [ ] No schema/data compatibility change is made silently; if renaming the SQLite column requires migration or existing-data handling, implementation stops for an explicit decision.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~ApplicationDataServiceTests|FullyQualifiedName~DatabaseSchema|FullyQualifiedName~DecisionIntegrity"`
- [ ] `dotnet test --no-build`
- [ ] `dotnet format --verify-no-changes`
- [ ] `dotnet build --no-restore`

**Dependencies:** Task 16B

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Data/ReleaseDetailProjection.cs`
- `src/ReleaseReadinessCoordinator/Data/IApplicationDataService.cs`
- `src/ReleaseReadinessCoordinator/Data/ApplicationDataService.Workflow.cs`
- `src/ReleaseReadinessCoordinator/Data/PersistenceRows.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Data/ApplicationDataServiceTests.cs`

**Estimated scope:** Medium (5 files)

## Checkpoint D: Core end-to-end workflow

- [x] Tasks 14-16 acceptance criteria are met.
- [x] The all-pass, remediation/selective-reuse, terminal approval, and terminal rejection paths run on the simplified real graph.
- [x] Round/business history is complete, idempotent, and explainable.
- [x] No branch-local wait, rerun-all shortcut, automatic approval, or human-decision edge back to the planner exists.
- [ ] Identity names distinguish entity IDs, cross-entity references, and MAF continuation IDs without relying on comments.

## Task 17: Add restart recovery, synchronization, and reconciliation

**Description:** Implement the HTTP-driven workflow host that starts or restores runs under one checkpoint-store critical section. Rebuild the identical graph, select the latest valid checkpoint, verify re-emitted request correlation, reconcile idempotent business writes, and surface technical failures.

**Acceptance criteria:**

- [ ] One application-lifetime filesystem store is protected by external async synchronization; no startup worker or background resumer is introduced.
- [ ] Detail/response requests restore the same workflow and pending request after disposal/restart, then continue exactly once without duplicate business records.
- [ ] Missing/corrupt/incompatible checkpoints, impossible state, and unrecoverable SQLite failures persist a visible `Failed` phase and diagnostic timeline entry.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~RestartRecovery"`
- [ ] Integration test creates a wait, disposes the host, creates a new host over the same SQLite/checkpoint directories, responds, and verifies one continuation.
- [ ] Concurrent response test proves serialization/idempotency.

**Dependencies:** Tasks 3, 9, 15-16, and 16A-16C

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/WorkflowHost.cs`
- `src/ReleaseReadinessCoordinator/Workflow/CheckpointStoreCoordinator.cs`
- `src/ReleaseReadinessCoordinator/Workflow/WorkflowReconciler.cs`
- `src/ReleaseReadinessCoordinator/Program.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/RestartRecoveryTests.cs`

**Estimated scope:** Medium (5 files)

## Task 18: Build release detail and timeline UI

**Description:** Render the current process state and immutable history from the release-detail projection, using manual refresh and accessible server-rendered HTML.

**Acceptance criteria:**

- [ ] The page shows phase, three current results, evidence/findings, round history, planning reasons, `ValidUntil`, attempts, deterministic brief, active wait, and chronological timeline.
- [ ] Every round labels `Executed because ...` or `Reused from round N because ...` and links a reused result to its source.
- [ ] Technical and continuation failures are visible without exposing secrets or raw checkpoint contents.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~ReleaseDetailPage"`
- [ ] Manually inspect empty, evaluating, remediation, approval, terminal, and failed states at narrow and desktop widths.
- [ ] Keyboard navigation and heading/table semantics are coherent.

**Dependencies:** Tasks 10 and 17

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Pages/Releases/Details.cshtml`
- `src/ReleaseReadinessCoordinator/Pages/Releases/Details.cshtml.cs`
- `src/ReleaseReadinessCoordinator/wwwroot/css/site.css`
- `tests/ReleaseReadinessCoordinator.Tests/Web/ReleaseDetailPageTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint E1: Recovery and read model

- [ ] Tasks 17-18 acceptance criteria are met.
- [ ] Remediation and approval waits survive host replacement with no duplicate history.
- [ ] The detail page explains current state, immutable round history, reuse sources, active waits, and failures.

## Task 19: Build remediation interaction UI

**Description:** Add the typed remediation page and PRG handler for the active correlated request, presenting all problems and accepting new evidence versions plus explicit branch selections for rerun. Immutable release metadata remains visible but cannot be edited.

**Acceptance criteria:**

- [ ] Only the active request can render/submit, and its correlation token is round-tripped and verified.
- [ ] The form exposes all current problems, permits only new branch evidence and rerun selections, and cannot post edits to release metadata.
- [ ] Success redirects to detail after workflow continuation; stale/duplicate/mismatched submissions show safe feedback and never resume the wrong workflow.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~RemediationPage"`
- [ ] Manual blocker -> remediation -> selective rerun confirms correct Execute/Reused display.
- [ ] Invalid model state and double-submit behavior are checked.

**Dependencies:** Tasks 15 and 17-18

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Pages/Releases/Remediate.cshtml`
- `src/ReleaseReadinessCoordinator/Pages/Releases/Remediate.cshtml.cs`
- `src/ReleaseReadinessCoordinator/Workflow/RemediationHandler.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Web/RemediationPageTests.cs`

**Estimated scope:** Medium (4 files)

## Task 20: Build decision interaction UI

**Description:** Add the typed human-decision page and PRG handler, displaying the immutable snapshot/brief and active MAF request identity, then accepting Approve/Reject, actor, and comment.

**Acceptance criteria:**

- [ ] The page renders only the snapshot carried by the restored active approval request; snapshot identity and a separate concurrency token are not accepted from the form.
- [ ] Approve/Reject requires actor and comment, resumes the matching typed request, persists one terminal decision, and redirects to terminal detail.
- [ ] Missing, mismatched, corrupt, or incompatible continuation displays safe feedback, creates no human-response record, and leaves the workflow unresumed; exact double-submit has one terminal effect.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~DecisionPage"`
- [ ] Manual approval, rejection, and invalid-continuation journeys succeed.
- [ ] Double-submit produces one terminal decision.

**Dependencies:** Tasks 16-18

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Pages/Releases/Decision.cshtml`
- `src/ReleaseReadinessCoordinator/Pages/Releases/Decision.cshtml.cs`
- `src/ReleaseReadinessCoordinator/Workflow/HumanDecisionHandler.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Web/DecisionPageTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint E2: Demonstrable MVP

- [ ] Tasks 17-20 acceptance criteria are met.
- [ ] Remediation and approval waits both survive a real host restart.
- [ ] The four-page Razor UI supports the complete approved journey with manual refresh.
- [ ] Accessibility, request correlation, continuation-error feedback, failure visibility, and duplicate protection are manually reviewed.

## Task 21: Complete real-graph workflow scenario coverage

**Description:** Consolidate the six required orchestration-risk scenarios into a small real-graph suite using deterministic providers, fake time, temporary SQLite, and temporary checkpoint directories. Assert business history and provider/policy call counts, not DTO trivia.

**Acceptance criteria:**

- [ ] The suite covers all-pass approval, multi-block remediation/selective reuse, missing/transient aggregation, remediation-wait restart/resume-once, approval-wait restart/resume-once, and terminal rejection.
- [ ] Each scenario verifies three-result fan-in, wait timing, phase transitions, timeline explanations, idempotency, and provider/policy call counts.
- [ ] Unexpected exceptions remain technical failures and terminal decisions cannot reopen.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~WorkflowScenario"`
- [ ] Run the suite twice against clean temporary stores to expose ordering/static-state leaks.
- [ ] `dotnet format --verify-no-changes`

**Dependencies:** Tasks 17-20

**Files likely touched:**

- `tests/ReleaseReadinessCoordinator.Tests/Workflow/WorkflowScenarioTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Support/TestApplicationFactory.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Support/CountingFakes.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Support/ScenarioBuilder.cs`

**Estimated scope:** Medium (4 files)

## Task 22: Add minimal browser smoke coverage

**Description:** Add real-browser smoke tests for only the two required user journeys: submit/pass/human decision and blocker/remediation/selective rerun. Use accessible locators and retain server-side PRG/manual-refresh behavior.

**Acceptance criteria:**

- [ ] Browser coverage proves submit -> passing round -> restored human decision reaches a terminal phase.
- [ ] Browser coverage proves blocker -> remediation -> selective rerun and visibly distinguishes Executed from Reused with source linkage.
- [ ] Tests use isolated temporary stores, deterministic fakes, and capture actionable diagnostics on failure without adding SPA or real-time infrastructure.

**Verification:**

- [ ] Install the pinned browser runtime documented by the test project.
- [ ] `dotnet test --no-build --filter "FullyQualifiedName~BrowserSmoke"`
- [ ] Inspect failure screenshots/traces only when a test fails.

**Dependencies:** Tasks 18-21

**Files likely touched:**

- `tests/ReleaseReadinessCoordinator.Tests/Browser/PassingDecisionSmokeTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Browser/RemediationReuseSmokeTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Browser/BrowserFixture.cs`
- `tests/ReleaseReadinessCoordinator.Tests/ReleaseReadinessCoordinator.Tests.csproj`

**Estimated scope:** Medium (4 files)

## Checkpoint F1: Evaluation

- [ ] Tasks 21-22 acceptance criteria are met.
- [ ] Required real-graph scenarios and both browser journeys pass.
- [ ] Test evidence is deterministic.

## Task 23: Finish documentation, full verification, and spec audit

**Description:** Document setup, simulated fixtures, app-data locations, restart demo, test commands, and architecture boundaries. Run the complete quality gate, inspect the diff, and map evidence to all 13 MVP acceptance criteria.

**Acceptance criteria:**

- [ ] `README.md` explains local setup, demo journeys, restart procedure, checkpoint trust/single-process constraints, and normal tests.
- [ ] `AGENTS.md` contains accurate paths/commands, and a final acceptance matrix maps every `SPEC.md` criterion to executable or manual evidence.
- [ ] Final review finds no prohibited component, generic platform, silent spec deviation, secret, generated runtime data, or unrelated change.

**Verification:**

- [ ] `dotnet restore`
- [ ] `dotnet build --no-restore`
- [ ] `dotnet test --no-build`
- [ ] `dotnet format --verify-no-changes`
- [ ] Run both manual end-to-end journeys, including stop/restart at remediation and approval waits.
- [ ] Review the complete diff and report unrun checks, residual risks, and any approved deviation.

**Dependencies:** Tasks 21-22

**Files likely touched:**

- `README.md`
- `AGENTS.md`
- `docs/acceptance-matrix.md`
- `.gitignore`

**Estimated scope:** Medium (4 files)

## Checkpoint F2: Complete

- [ ] Tasks 1-23 and every intermediate checkpoint are complete.
- [ ] All task acceptance criteria and the standing Definition of Done are satisfied.
- [ ] All 13 MVP acceptance criteria have recorded evidence.
- [ ] The solution remains one bounded deployable application with one focused test project.
- [ ] The owner has reviewed and approved the completed implementation before merge or deployment.
