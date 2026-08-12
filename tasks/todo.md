# Implementation Tasks: Release Readiness Coordinator MVP

This checklist implements `SPEC.md` without changing its authority. Complete tasks in dependency order and stop at each checkpoint for review. Commands assume repository root.

## Task 1: Bootstrap the .NET 10 solution and command baseline

**Description:** Create one solution, one deployable Razor Pages application, and one focused xUnit test project. Pin the justified package references, enable nullable analysis, exclude application data from source control, and record the root commands in a minimal `AGENTS.md`.

**Acceptance criteria:**

- [x] `ReleaseReadinessCoordinator.slnx` contains exactly the web and test projects, both targeting .NET 10.
- [x] The web project directly pins `Microsoft.Agents.AI.Workflows` to `1.17.0` and includes only spec-justified EF Core/SQLite and `IChatClient` dependencies.
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

## Task 2: Prove the fixed MAF four-branch graph

**Description:** Build the smallest production-shaped static MAF graph with stable executor IDs, typed planner/work/result messages, and one explicit conditional edge per readiness branch. Prove that one planner invocation routes work to four branch executors in one evaluation round and the aggregator runs only after all four results exist.

**Acceptance criteria:**

- [x] The planner emits exactly one Test, Security, Change, and Dependency `BranchWorkItem` with an Execute/Reuse disposition.
- [x] Four stable-ID executors each emit exactly one typed `BranchResult`; the aggregator rejects duplicates, omissions, and impossible branch identities as technical failures.
- [x] A real MAF 1.17.0 test demonstrates complete four-source fan-in and no pre-fan-in external request.

**Verification:**

- [x] `dotnet test --no-build --filter "FullyQualifiedName~WorkflowTopology"`
- [x] `dotnet build --no-restore`
- [x] Inspect emitted executor events to confirm all four branch IDs precede aggregation.

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

- [ ] Remediation and approval have distinct typed request/response contracts and occur only after fan-in.
- [ ] A pending request, including identity and type, is recovered from `FileSystemJsonCheckpointStore` after disposing the first run and rebuilding the graph.
- [ ] Missing, corrupt, mismatched, or incompatible continuation state produces a visible technical failure and never starts a new workflow.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~CheckpointContract"`
- [ ] `dotnet test --no-build --filter "FullyQualifiedName~ExternalRequestContract"`
- [ ] Manually inspect the temporary checkpoint directory and restored request correlation.

**Dependencies:** Task 2

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/WorkflowMessages.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReleaseWorkflowFactory.cs`
- `src/ReleaseReadinessCoordinator/Workflow/CheckpointStoreCoordinator.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/CheckpointContractTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint A: Framework viability

- [ ] Tasks 1-3 acceptance criteria are met.
- [ ] All root commands pass.
- [ ] MAF 1.17.0 topology, external-request, rehydration, and stable-ID assumptions are proven in executable tests.
- [ ] Human review approves continuation without changing the pinned version or graph strategy.

## Task 4: Define the release-readiness domain vocabulary

**Description:** Model the concrete domain records and value types required by the specification while keeping process phase, branch outcome, execution disposition, and result validity separate. Preserve immutability/versioning and avoid a generic workflow framework.

**Acceptance criteria:**

- [ ] Types cover release revisions, four evidence kinds, work items/results, rounds, remediation, snapshots, responses, correlation, and timeline entries.
- [ ] Enums and invariants prevent universal-status conflation, duplicate branch results, reopening terminal revisions, and invalid result/source combinations.
- [ ] All timestamps are UTC instants supplied externally; no domain logic reads the system clock directly.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~DomainInvariant"`
- [ ] `dotnet build --no-restore`
- [ ] Review the domain surface against `SPEC.md` sections 5, 6, 10, and 12.4.

**Dependencies:** Task 3

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Domain/Releases.cs`
- `src/ReleaseReadinessCoordinator/Domain/Evidence.cs`
- `src/ReleaseReadinessCoordinator/Domain/Evaluations.cs`
- `src/ReleaseReadinessCoordinator/Domain/Decisions.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Domain/DomainInvariantTests.cs`

**Estimated scope:** Medium (5 files)

## Task 5: Implement fingerprints, validity, and invalidation primitives

**Description:** Add canonical fingerprints, freshness calculations, reason-coded validity, and the exact explicit invalidation map. These primitives will be shared by planning, result reuse, and human-response revalidation.

**Acceptance criteria:**

- [ ] Canonical SHA-256 fingerprints are stable for semantically identical input and change for every relevant release/evidence field.
- [ ] Test, Security, and Dependency use the 24-hour maximum with earlier evidence bounds honored; validity distinguishes current, expired, and invalidated with reason codes.
- [ ] Every row of the specification's explicit invalidation map is implemented and tested without a generic dependency graph.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~Fingerprint"`
- [ ] `dotnet test --no-build --filter "FullyQualifiedName~Invalidation"`
- [ ] Boundary checks use a fake `TimeProvider`.

**Dependencies:** Task 4

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Domain/Fingerprints.cs`
- `src/ReleaseReadinessCoordinator/Domain/ResultValidity.cs`
- `src/ReleaseReadinessCoordinator/Domain/InvalidationMap.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Domain/ValidityAndInvalidationTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint B1: Domain semantics

- [ ] Tasks 4-5 acceptance criteria are met.
- [ ] Domain types keep process phase, branch outcome, disposition, and validity separate.
- [ ] Fingerprints, freshness boundaries, and every explicit invalidation-map row are verified.

## Task 6: Create the SQLite schema and migrations

**Description:** Map business/audit state to SQLite using EF Core. Use unique constraints and concurrency tokens for release revision identity, operation idempotency, one-result-per-branch-per-round, active requests, immutable snapshots, and terminal decisions.

**Acceptance criteria:**

- [ ] The schema represents all stores named in `SPEC.md` section 12.3 and preserves immutable/versioned evidence plus append-only history.
- [ ] Unique indexes enforce duplicate submission conflicts, stable operation keys, and one branch result per check/round.
- [ ] A migration creates a fresh database and EF optimistic concurrency protects active request/snapshot mutation points.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~DatabaseSchema"`
- [ ] Apply the migration to a temporary SQLite file and inspect expected tables/indexes.
- [ ] `dotnet build --no-restore`

**Dependencies:** Tasks 4-5

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Data/AppDbContext.cs`
- `src/ReleaseReadinessCoordinator/Data/EntityConfiguration.cs`
- `src/ReleaseReadinessCoordinator/Data/Migrations/InitialCreate.cs`
- `src/ReleaseReadinessCoordinator/Data/Migrations/AppDbContextModelSnapshot.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Data/DatabaseSchemaTests.cs`

**Estimated scope:** Medium (5 primary files; generated migration metadata is mechanical)

## Task 7: Implement the bounded idempotent application data service

**Description:** Provide small, use-case-oriented async operations over `AppDbContext` for submissions, evidence, rounds/results, requests, snapshots/responses, correlations, failures, and timeline entries. Make workflow replay harmless through stable operation keys and transactions local to SQLite.

**Acceptance criteria:**

- [ ] Replaying the same operation key returns the existing business record without duplicate rows or timeline entries.
- [ ] Duplicate release identifier/revision returns conflict, and Approved/Rejected revisions cannot reopen.
- [ ] Reads reconstruct a coherent release-detail projection while writes remain append-only where the spec requires history.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~ApplicationDataService"`
- [ ] Concurrency tests submit the same operation twice and verify one durable effect.
- [ ] `dotnet format --verify-no-changes`

**Dependencies:** Task 6

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Data/IApplicationDataService.cs`
- `src/ReleaseReadinessCoordinator/Data/ApplicationDataService.cs`
- `src/ReleaseReadinessCoordinator/Data/ReleaseDetailProjection.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Data/ApplicationDataServiceTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint B2: Durable foundation

- [ ] Tasks 4-7 acceptance criteria are met.
- [ ] Domain and data tests pass against temporary SQLite databases.
- [ ] State dimensions, invalidation rules, immutability, constraints, and replay safety match the specification.
- [ ] No generic repository, CQRS, event-sourcing, or speculative layer exists.

## Task 8: Deliver release submission and demo fixtures

**Description:** Implement the first vertical web slice: a coordinator submits one release revision with compact evidence fields or a chosen local demo fixture, the application persists it, starts the workflow, and redirects to release detail.

**Acceptance criteria:**

- [ ] The form captures every release submission field and initial evidence category required by `SPEC.md` section 5.
- [ ] Valid submission creates immutable initial records and starts the correlated workflow; invalid input redisplays accessible field errors.
- [ ] Duplicate identifier/revision produces HTTP 409 semantics and a useful page message; terminal revisions are not reopened.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~ReleaseSubmission"`
- [ ] `dotnet run --project src/ReleaseReadinessCoordinator` and manually submit a demo fixture.
- [ ] Confirm POST/redirect/GET and one persisted release revision.

**Dependencies:** Tasks 3 and 7

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Pages/Releases/New.cshtml`
- `src/ReleaseReadinessCoordinator/Pages/Releases/New.cshtml.cs`
- `src/ReleaseReadinessCoordinator/Simulation/DemoReleaseFixtures.cs`
- `src/ReleaseReadinessCoordinator/Program.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Web/ReleaseSubmissionTests.cs`

**Estimated scope:** Medium (5 files)

## Task 9: Deliver the Test readiness slice

**Description:** Add the simulated Test evidence provider, typed transient failure classification/retry harness, deterministic Test policy, and branch executor integration.

**Acceptance criteria:**

- [ ] Missing required facts map to `MissingEvidence`; version, pass-rate, critical-suite, or freshness misses map to `Blocked`; otherwise the result is `Passed`.
- [ ] Only typed known provider failures retry, with exactly three total immediate attempts; exhaustion returns `TransientFailure` with attempt details.
- [ ] Boundary tests cover 95%, exact version match, critical failures, and the validity deadline.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~TestReadiness"`
- [ ] Counting fakes verify provider and policy call counts.
- [ ] `dotnet build --no-restore`

**Dependencies:** Tasks 4-5

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Readiness/BranchExecution.cs`
- `src/ReleaseReadinessCoordinator/Readiness/TestEvidenceProvider.cs`
- `src/ReleaseReadinessCoordinator/Readiness/TestPolicy.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Readiness/TestReadinessTests.cs`

**Estimated scope:** Medium (4 files)

## Task 10: Deliver the Security readiness slice

**Description:** Add simulated Security evidence retrieval and deterministic evaluation of version, critical/high findings, scoped exceptions, expiry through the release window, and evidence freshness.

**Acceptance criteria:**

- [ ] Missing scan/required exception facts, deterministic blockers, exhausted known transient failures, and passing evidence map to the exact four outcomes.
- [ ] Every high finding requires a matching in-scope exception valid through the entire requested release window; unresolved critical findings always block.
- [ ] Boundary tests cover exception scope/expiry, release-window changes, exact version match, and freshness.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~SecurityReadiness"`
- [ ] Counting fakes prove no retry for missing evidence or deterministic blockers.
- [ ] `dotnet format --verify-no-changes`

**Dependencies:** Tasks 5 and 9

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Readiness/SecurityEvidenceProvider.cs`
- `src/ReleaseReadinessCoordinator/Readiness/SecurityPolicy.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReadinessExecutors.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Readiness/SecurityReadinessTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint C1: Submission and first policies

- [ ] Tasks 8-10 acceptance criteria are met.
- [ ] A release can be submitted once and starts a correlated workflow.
- [ ] Test and Security boundary, retry, and outcome-mapping tests pass.

## Task 11: Deliver validated and cached rollback analysis

**Description:** Implement the sole LLM-backed capability against injected `IChatClient`. Normalize rollback text, request exactly five structured checklist findings, validate citations/offsets strictly, and cache only valid results by content SHA-256 plus `AnalyzerVersion`.

**Acceptance criteria:**

- [ ] Output permits only the five named items and `Present|Absent|Ambiguous`, with normalized observations and zero or more exact excerpts plus valid character offsets.
- [ ] Unknown/duplicate/missing items, mismatched excerpts, invalid offsets, unsupported claims, and malformed output are rejected; unclear support abstains.
- [ ] Valid analysis is cached by normalized content hash and analyser version; exhausted transient failures are not cached, and retries total three immediate attempts.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~RollbackAnalyzer"`
- [ ] Recorded/fake responses cover schema rejection, citation fidelity, cache hit/miss, and transient exhaustion.
- [ ] Confirm no analyser method returns a branch outcome or decision.

**Dependencies:** Tasks 4-5

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Llm/RollbackAnalysisContracts.cs`
- `src/ReleaseReadinessCoordinator/Llm/RollbackPlanAnalyzer.cs`
- `src/ReleaseReadinessCoordinator/Llm/RollbackAnalysisValidator.cs`
- `src/ReleaseReadinessCoordinator/Llm/RollbackAnalysisCache.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Llm/RollbackAnalyzerTests.cs`

**Estimated scope:** Medium (5 files)

## Task 12: Deliver the Change readiness slice

**Description:** Combine simulated change approval/window evidence with validated rollback findings, then apply deterministic C# policy. The branch executor may call the analyser only when executing and only after required rollback text exists.

**Acceptance criteria:**

- [ ] Missing approval or rollback text maps to `MissingEvidence`; unapproved, out-of-window, absent, or ambiguous checklist evidence maps to `Blocked`.
- [ ] `Passed` requires change approval, full containment in the approved window, and all five findings `Present`.
- [ ] Known source/analyser retry exhaustion maps to `TransientFailure`; validation/invariant/programming failures remain technical failures.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~ChangeReadiness"`
- [ ] Data-driven tests cover window boundaries and each missing/ambiguous checklist item.
- [ ] Counting fake proves one cache/analyser path per executing branch and no LLM authority over outcome.

**Dependencies:** Tasks 9 and 11

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Readiness/ChangeEvidenceProvider.cs`
- `src/ReleaseReadinessCoordinator/Readiness/ChangePolicy.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReadinessExecutors.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Readiness/ChangeReadinessTests.cs`

**Estimated scope:** Medium (4 files)

## Task 13: Deliver the Dependency readiness slice

**Description:** Add simulated Dependency evidence retrieval and deterministic compatibility, availability coverage, maintenance overlap, and freshness policy.

**Acceptance criteria:**

- [ ] Missing required/available versions, intervals, or observation facts map to `MissingEvidence`; known retry exhaustion maps to `TransientFailure`.
- [ ] Incompatible versions, incomplete release-window availability, maintenance overlap, or expired evidence map to `Blocked`; otherwise the result is `Passed`.
- [ ] Tests cover interval inclusivity, overlap boundaries, version compatibility, observation time, and the 24-hour validity maximum.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~DependencyReadiness"`
- [ ] Counting fake verifies exact retry/provider/policy calls.
- [ ] `dotnet build --no-restore`

**Dependencies:** Tasks 5 and 9

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Readiness/DependencyEvidenceProvider.cs`
- `src/ReleaseReadinessCoordinator/Readiness/DependencyPolicy.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReadinessExecutors.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Readiness/DependencyReadinessTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint C2: Deterministic readiness

- [ ] Tasks 8-13 acceptance criteria are met.
- [ ] Data-driven policy and retry tests pass.
- [ ] All four expected outcomes are representable without hiding technical failures.
- [ ] The LLM boundary, validation, citations, cache, and deterministic Change authority pass review.

## Task 14: Implement selective execution and safe reuse planning

**Description:** Build the round planner that always emits four work items and chooses Execute/Reuse from prior outcome, expiry, fingerprints, relevant input changes, policy/analyser versions, and explicit invalidation. Implement a separate defensive reuse path.

**Acceptance criteria:**

- [ ] Every invalidation-map row and every reuse prerequisite produces the specified Execute/Reuse decision and reason.
- [ ] Reuse verifies hashes/versions/validity, emits a new result linked to source result/round, and explains why reuse is safe.
- [ ] Reused work makes zero provider, policy, analyser, or analyser-cache calls; failed defensive verification becomes a technical failure rather than silent execution.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~SelectiveRerun"`
- [ ] Counting fakes prove zero forbidden calls on reuse and expected calls on execution.
- [ ] Time-driven tests expire one branch without invalidating unrelated branches.

**Dependencies:** Tasks 5 and 9-13

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/RoundPlanner.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ResultReuse.cs`
- `src/ReleaseReadinessCoordinator/Workflow/WorkflowMessages.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/SelectiveRerunTests.cs`

**Estimated scope:** Medium (4 files)

## Task 15: Complete aggregation and remediation resumption

**Description:** Connect planner and branch slices through the real fixed graph. Persist complete rounds, create one remediation request containing all current problems after fan-in, accept correlated corrected inputs/explicit invalidations, and start the next selective round.

**Acceptance criteria:**

- [ ] Each round persists exactly four results and complete Executed/Reused explanations before routing.
- [ ] Any non-pass combination creates exactly one remediation request containing every current problem; no branch waits independently.
- [ ] A current correlated remediation response versions changed evidence/release inputs, records explicit invalidations, closes the request once, and resumes selective evaluation.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~RemediationWorkflow"`
- [ ] Real-graph test blocks multiple branches, remediates them, and proves unaffected branches reuse.
- [ ] Duplicate or mismatched remediation responses have no duplicate effect.

**Dependencies:** Tasks 7 and 14

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/ReleaseWorkflowFactory.cs`
- `src/ReleaseReadinessCoordinator/Workflow/RoundAggregator.cs`
- `src/ReleaseReadinessCoordinator/Workflow/RemediationHandler.cs`
- `src/ReleaseReadinessCoordinator/Data/ApplicationDataService.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/RemediationWorkflowTests.cs`

**Estimated scope:** Medium (5 files)

## Task 16: Build immutable snapshots and handle human decisions

**Description:** For an all-pass round, persist one immutable `DecisionSnapshot` and deterministic brief, issue typed approval, revalidate all integrity/freshness inputs, and persist one terminal approval/rejection or a declined stale response that selectively resumes evaluation.

**Acceptance criteria:**

- [ ] Snapshot resolves four source results/hashes, relevant fingerprints, policy/analyser versions, earliest validity bound, deterministic brief, and brief hash.
- [ ] A response must match active request, latest fully passing snapshot, concurrency token, hashes, versions, deadlines, and brief hash before it can terminate Approved/Rejected.
- [ ] Stale responses persist as declined with reasons, invalidate only affected checks, close the old request, and require a fresh response after reevaluation.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~DecisionIntegrity"`
- [ ] Tests cover current approve, current reject, every stale dimension, duplicate responses, and expiry while waiting.
- [ ] Brief output is byte-stable for identical snapshot inputs.

**Dependencies:** Task 15

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Workflow/DecisionSnapshotBuilder.cs`
- `src/ReleaseReadinessCoordinator/Workflow/HumanDecisionHandler.cs`
- `src/ReleaseReadinessCoordinator/Workflow/ReleaseWorkflowFactory.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Workflow/DecisionIntegrityTests.cs`

**Estimated scope:** Medium (4 files)

## Checkpoint D: Core end-to-end workflow

- [ ] Tasks 14-16 acceptance criteria are met.
- [ ] The all-pass, remediation/selective-reuse, stale-response, approval, and rejection paths run on the real graph.
- [ ] Round/business history is complete, idempotent, and explainable.
- [ ] No branch-local wait, rerun-all shortcut, LLM routing, or automatic approval exists.

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

**Dependencies:** Tasks 3, 7, 15-16

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

- [ ] The page shows phase, four current results, evidence/findings, round history, validity, attempts, deterministic brief, active wait, and chronological timeline.
- [ ] Every round labels `Executed because ...` or `Reused from round N because ...` and links a reused result to its source.
- [ ] Technical failure and stale-response reasons are visible without exposing secrets or raw checkpoint contents.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~ReleaseDetailPage"`
- [ ] Manually inspect empty, evaluating, remediation, approval, terminal, and failed states at narrow and desktop widths.
- [ ] Keyboard navigation and heading/table semantics are coherent.

**Dependencies:** Tasks 8 and 17

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

**Description:** Add the typed remediation page and PRG handler for the active correlated request, presenting all problems and accepting corrected evidence/release inputs plus explicit branch invalidations.

**Acceptance criteria:**

- [ ] Only the active request can render/submit, and its correlation token is round-tripped and verified.
- [ ] The form exposes all current problems, permits only spec-authorized corrections/invalidation choices, and versions changed inputs rather than mutating history.
- [ ] Success redirects to detail after workflow continuation; stale/duplicate/mismatched submissions show safe feedback and never resume the wrong workflow.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~RemediationPage"`
- [ ] Manual blocker -> remediation -> selective rerun confirms correct Execute/Reused display.
- [ ] Invalid model state and double-submit behavior are checked.

**Dependencies:** Tasks 15, 17-18

**Files likely touched:**

- `src/ReleaseReadinessCoordinator/Pages/Releases/Remediate.cshtml`
- `src/ReleaseReadinessCoordinator/Pages/Releases/Remediate.cshtml.cs`
- `src/ReleaseReadinessCoordinator/Workflow/RemediationHandler.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Web/RemediationPageTests.cs`

**Estimated scope:** Medium (4 files)

## Task 20: Build decision interaction UI

**Description:** Add the typed human-decision page and PRG handler, displaying the immutable snapshot/brief and accepting Approve/Reject, actor, comment, request identity, snapshot identity, and concurrency token.

**Acceptance criteria:**

- [ ] The page renders only the active latest fully passing snapshot and makes all submitted identities explicit.
- [ ] Approve/Reject requires actor and comment, invokes deterministic revalidation, and redirects to terminal detail when current.
- [ ] A stale response displays decline reasons and the newly resumed selective-evaluation state; the old response is never auto-applied.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~DecisionPage"`
- [ ] Manual current approval, current rejection, and expired-evidence stale-response journeys succeed.
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
- [ ] Accessibility, correlation, stale feedback, failure visibility, and duplicate protection are manually reviewed.

## Task 21: Complete real-graph workflow scenario coverage

**Description:** Consolidate the six required orchestration-risk scenarios into a small real-graph suite using deterministic providers, fake time, temporary SQLite, and temporary checkpoint directories. Assert business history and dependency call counts, not DTO trivia.

**Acceptance criteria:**

- [ ] The suite covers all-pass approval, multi-block remediation/selective reuse, missing/transient aggregation, restart/resume-once, approval-time expiry/selective rerun, and current rejection.
- [ ] Each scenario verifies four-result fan-in, wait timing, phase transitions, timeline explanations, idempotency, and provider/policy/analyser call counts.
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

## Task 22: Add the curated rollback-analysis evaluation corpus

**Description:** Create approximately ten versioned rollback plans and recorded structured responses spanning complete, missing-item, ambiguous-owner, misleading-heading, contradictory, malformed, and citation-integrity cases. Evaluate analyser classification/abstention/grounding plus deterministic Change mapping and cache behavior.

**Acceptance criteria:**

- [ ] The corpus includes complete plans, each missing checklist category, ambiguous ownership, misleading headings, contradictory steps, and bad citation/offset attempts.
- [ ] Tests score each expected item classification, abstention, output validity, excerpt fidelity, offsets, policy mapping, and cache key/invalidation behavior.
- [ ] Normal CI is fully deterministic; an optional credentialed provider smoke is explicit, skipped by default, and cannot alter authoritative policy behavior.

**Verification:**

- [ ] `dotnet test --no-build --filter "FullyQualifiedName~RollbackEvaluation"`
- [ ] Review every exact excerpt against its source text and expected offsets.
- [ ] If configured by the owner, run and separately report the optional provider smoke.

**Dependencies:** Tasks 11-12

**Files likely touched:**

- `tests/ReleaseReadinessCoordinator.Tests/Llm/Corpus/rollback-evaluation.json`
- `tests/ReleaseReadinessCoordinator.Tests/Llm/RollbackEvaluationTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Llm/CredentialedProviderSmokeTests.cs`
- `src/ReleaseReadinessCoordinator/Program.cs`

**Estimated scope:** Medium (4 files)

## Task 23: Add minimal browser smoke coverage

**Description:** Add real-browser smoke tests for only the two required user journeys: submit/pass/human decision and blocker/remediation/selective rerun. Use accessible locators and retain server-side PRG/manual-refresh behavior.

**Acceptance criteria:**

- [ ] Browser coverage proves submit -> passing round -> current human decision reaches a terminal phase.
- [ ] Browser coverage proves blocker -> remediation -> selective rerun and visibly distinguishes Executed from Reused with source linkage.
- [ ] Tests use isolated temporary stores, deterministic fakes, and capture actionable diagnostics on failure without adding SPA or real-time infrastructure.

**Verification:**

- [ ] Install the pinned browser runtime documented by the test project.
- [ ] `dotnet test --no-build --filter "FullyQualifiedName~BrowserSmoke"`
- [ ] Inspect failure screenshots/traces only when a test fails.

**Dependencies:** Tasks 18-22

**Files likely touched:**

- `tests/ReleaseReadinessCoordinator.Tests/Browser/PassingDecisionSmokeTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Browser/RemediationReuseSmokeTests.cs`
- `tests/ReleaseReadinessCoordinator.Tests/Browser/BrowserFixture.cs`
- `tests/ReleaseReadinessCoordinator.Tests/ReleaseReadinessCoordinator.Tests.csproj`

**Estimated scope:** Medium (4 files)

## Checkpoint F1: Evaluation

- [ ] Tasks 21-23 acceptance criteria are met.
- [ ] Required real-graph scenarios, rollback corpus evaluations, and both browser journeys pass.
- [ ] Test evidence is deterministic by default and any optional credentialed check is reported separately.

## Task 24: Finish documentation, full verification, and spec audit

**Description:** Document setup, provider configuration, simulated fixtures, app-data locations, restart demo, test commands, and architecture boundaries. Run the complete quality gate, inspect the diff, and map evidence to all 13 MVP acceptance criteria.

**Acceptance criteria:**

- [ ] `README.md` explains local setup, optional provider configuration, demo journeys, restart procedure, checkpoint trust/single-process constraints, and normal versus credentialed tests.
- [ ] `AGENTS.md` contains accurate paths/commands, and a final acceptance matrix maps every `SPEC.md` criterion to executable or manual evidence.
- [ ] Final review finds no prohibited component, generic platform, extra LLM capability, silent spec deviation, secret, generated runtime data, or unrelated change.

**Verification:**

- [ ] `dotnet restore`
- [ ] `dotnet build --no-restore`
- [ ] `dotnet test --no-build`
- [ ] `dotnet format --verify-no-changes`
- [ ] Run both manual end-to-end journeys, including stop/restart at remediation and approval waits.
- [ ] Review the complete diff and report unrun checks, residual risks, and any approved deviation.

**Dependencies:** Tasks 21-23

**Files likely touched:**

- `README.md`
- `AGENTS.md`
- `docs/acceptance-matrix.md`
- `.gitignore`

**Estimated scope:** Medium (4 files)

## Checkpoint F2: Complete

- [ ] Tasks 1-24 and every intermediate checkpoint are complete.
- [ ] All task acceptance criteria and the standing Definition of Done are satisfied.
- [ ] All 13 MVP acceptance criteria have recorded evidence.
- [ ] The solution remains one bounded deployable application with one focused test project.
- [ ] The owner has reviewed and approved the completed implementation before merge or deployment.
