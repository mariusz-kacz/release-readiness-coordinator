# Implementation Plan: Release Readiness Coordinator MVP

## Overview

Build the approved `SPEC.md` as one ASP.NET Core .NET 10 Razor Pages application named `ReleaseReadinessCoordinator`, backed by EF Core/SQLite for business history and Microsoft Agent Framework (MAF) filesystem checkpoints for workflow continuation. The implementation will deliver one fixed four-branch release-readiness workflow, deterministic policies and routing, safe selective reuse, typed remediation and approval waits, restart recovery, one tightly bounded `IChatClient` rollback analyser, and a minimal server-rendered UI. Work is ordered to prove the highest-risk MAF 1.17.0 behavior before building business features.

## Planning Basis

- `SPEC.md` is the sole authoritative product and architecture specification.
- The repository is greenfield: only `SPEC.md`, a placeholder `README.md`, and `.gitignore` exist.
- The NuGet V3 package index was checked on 2026-08-11 and includes `Microsoft.Agents.AI.Workflows` version `1.17.0`.
- Official MAF documentation confirms the planned superstep synchronization barrier, typed `RequestPort` external requests, checkpoint capture of pending requests, stable topology/executor identity requirements during rehydration, and the process-exclusive/non-thread-safe filesystem checkpoint store. Because some API reference pages display an older package label, Tasks 2 and 3 require compiled 1.17.0 contract tests before feature implementation continues.

Official references:

- [Workflow builder and execution](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/builder-and-execution)
- [Human-in-the-loop requests](https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop)
- [Workflow checkpoints](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints)
- [FileSystemJsonCheckpointStore](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.workflows.checkpointing.filesystemjsoncheckpointstore?view=agent-framework-dotnet-latest)
- [Microsoft.Agents.AI.Workflows package](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows/1.17.0)

## Settled Implementation Shape

- Solution: `ReleaseReadinessCoordinator.slnx`
- Web project: `src/ReleaseReadinessCoordinator/ReleaseReadinessCoordinator.csproj`
- Test project: `tests/ReleaseReadinessCoordinator.Tests/ReleaseReadinessCoordinator.Tests.csproj`
- Root namespace: `ReleaseReadinessCoordinator`
- Razor routes:
  - `/Releases/New`
  - `/Releases/{releaseId}/{revision}`
  - `/Releases/{releaseId}/{revision}/Remediate`
  - `/Releases/{releaseId}/{revision}/Decision`
- One bounded application data service uses EF Core directly. There are no generic repositories, CQRS layers, event sourcing, workers, queues, or additional deployables.
- Built-in `TimeProvider` is injected for all time-sensitive logic.
- Canonical UTF-8 serialization plus SHA-256 produces evidence, release-input, result, rollback-content, and decision-brief fingerprints.
- Policy versions and `AnalyzerVersion` are code-owned constants. Initial identifiers use explicit semantic strings such as `test-policy/1` and `rollback-analyzer/1`.
- Local simulated providers implement typed evidence-source contracts. Only known typed transient failures receive one initial attempt plus two immediate retries.
- The LLM integration consumes an injected `IChatClient`; deterministic fakes/recorded responses are used in normal tests. Provider-specific registration remains a documented composition choice after the owner identifies the available provider/deployment.
- SQLite stores immutable/versioned business records and an append-only timeline. MAF checkpoints live under a configurable private application-data directory that is excluded from source control.
- Stable operation keys make replayed SQLite writes idempotent. A single application-lifetime checkpoint store is protected by an async critical section.

## Dependency Graph

```mermaid
flowchart TD
    A[Solution and command baseline] --> B[MAF fan-out/fan-in proof]
    B --> C[MAF request/checkpoint proof]
    C --> D[Domain vocabulary and fingerprints]
    D --> E[SQLite schema and bounded data service]
    E --> F[Release submission]
    D --> G[Test, Security, Change, Dependency slices]
    F --> H[Selective round planner]
    G --> H
    H --> I[Complete aggregation and remediation]
    I --> J[Decision snapshot and response integrity]
    J --> K[Restart recovery and reconciliation]
    K --> L[Razor Pages interactions]
    L --> M[Workflow, LLM, and browser evaluation]
    M --> N[Documentation and final acceptance]
```

## Task List

Detailed acceptance criteria, verification commands, dependencies, and likely files are in `tasks/todo.md`.

### Phase 1: Fail-Fast Framework Foundation

- [ ] Task 1: Bootstrap the .NET 10 solution and command baseline
- [ ] Task 2: Prove the fixed MAF four-branch graph
- [ ] Task 3: Prove typed waits and checkpoint rehydration

### Checkpoint A: Framework Viability

- [ ] Exact MAF 1.17.0 reference restores and compiles
- [ ] A real graph emits exactly four branch results before aggregation
- [ ] A pending typed request survives filesystem-checkpoint rehydration
- [ ] Human review confirms no version or topology deviation is required

### Phase 2: Domain and Durable Business History

- [ ] Task 4: Define the release-readiness domain vocabulary
- [ ] Task 5: Implement fingerprints, validity, and invalidation primitives

### Checkpoint B1: Domain Semantics

- [ ] Domain types keep phase, outcome, disposition, and validity separate
- [ ] The explicit invalidation map is covered by deterministic tests

- [ ] Task 6: Create the SQLite schema and migrations
- [ ] Task 7: Implement the bounded idempotent application data service

### Checkpoint B2: Durable Foundation

- [ ] SQLite schema, constraints, and replay-safe writes are verified
- [ ] Immutable/versioned records and append-only history are preserved

### Phase 3: Submission and Four Readiness Slices

- [ ] Task 8: Deliver release submission and demo fixtures
- [ ] Task 9: Deliver the Test readiness slice
- [ ] Task 10: Deliver the Security readiness slice

### Checkpoint C1: Submission and First Policies

- [ ] A release can be submitted once and starts a correlated workflow
- [ ] Test and Security boundaries/outcome mappings are verified

- [ ] Task 11: Deliver validated and cached rollback analysis
- [ ] Task 12: Deliver the Change readiness slice
- [ ] Task 13: Deliver the Dependency readiness slice

### Checkpoint C2: Deterministic Readiness

- [ ] Every branch maps missing, blocked, transient, and passing outcomes correctly
- [ ] Retry count and classification are proven
- [ ] The LLM can only produce validated rollback findings; C# owns readiness

### Phase 4: Selective Workflow and Human Integrity

- [ ] Task 14: Implement selective execution and safe reuse planning
- [ ] Task 15: Complete aggregation and remediation resumption
- [ ] Task 16: Build immutable snapshots and handle human decisions

### Checkpoint D: Core End-to-End Workflow

- [ ] Every round contains four results with explicit Executed/Reused reasons
- [ ] Multiple current problems create one wait after complete fan-in
- [ ] Passing results form one immutable snapshot and deterministic brief
- [ ] Current decisions terminate; stale decisions selectively reevaluate

### Phase 5: Recovery and Minimal Razor UI

- [ ] Task 17: Add restart recovery, synchronization, and reconciliation
- [ ] Task 18: Build release detail and timeline UI

### Checkpoint E1: Recovery and Read Model

- [ ] Stop/restart/resume works for remediation and approval waits
- [ ] The detail page explains current state and immutable history

- [ ] Task 19: Build remediation interaction UI
- [ ] Task 20: Build decision interaction UI

### Checkpoint E2: Demonstrable MVP

- [ ] No replay creates duplicate business records
- [ ] Manual UI journeys expose evidence, findings, rounds, waits, and reasons

### Phase 6: Evaluation and Delivery

- [ ] Task 21: Complete real-graph workflow scenario coverage
- [ ] Task 22: Add the curated rollback-analysis evaluation corpus
- [ ] Task 23: Add minimal browser smoke coverage

### Checkpoint F1: Evaluation

- [ ] Required workflow, LLM, and browser scenarios pass
- [ ] Test evidence covers orchestration risks and both user journeys

- [ ] Task 24: Finish documentation, full verification, and spec audit

### Checkpoint F2: Complete

- [ ] All 13 MVP acceptance criteria in `SPEC.md` are demonstrated
- [ ] Restore, build, tests, formatting, runtime checks, and browser checks pass
- [ ] No prohibited architecture or LLM authority has been introduced
- [ ] Complete diff is reviewed and residual risks/unrun checks are reported
- [ ] Human review approves implementation readiness

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| MAF 1.17.0 APIs differ from current documentation examples | High | Tasks 2-3 compile and execute version-pinned topology, request, checkpoint, and stable-ID probes before domain implementation. Any conflict is reported; the version is never changed silently. |
| SQLite writes and filesystem checkpoints cannot be atomic | High | Use stable operation keys, unique constraints, replay-safe upserts, correlation verification, and explicit reconciliation tests. |
| Reuse accidentally calls providers, policies, or the analyser | High | Represent Execute/Reuse in planner output, keep reuse as a separate defensive code path, inject counting fakes, and assert zero forbidden calls. |
| A stale human response is accepted | High | Bind responses to request ID, snapshot ID, concurrency token, hashes, versions, deadlines, and brief hash; persist declined responses with reason codes. |
| LLM output invents or misquotes rollback evidence | High | Strict schema/item allowlist, exact substring/offset checks, abstention rules, recorded adversarial corpus, and deterministic Change policy. |
| Mutable release/evidence data erases audit history | Medium | Append immutable/versioned records and expose current projections without updating historical facts. |
| Checkpoint store is accessed concurrently or from multiple instances | Medium | Register one application-lifetime store, guard all start/resume access, document the single-process constraint, and test concurrent response handling. |
| Provider choice delays LLM integration | Medium | Build against `IChatClient`, use deterministic test doubles for normal development, and isolate provider-specific composition to one registration point. |
| UI scope expands beyond the portfolio MVP | Medium | Implement only four Razor routes, PRG interactions, manual refresh, and the exact fields/views in `SPEC.md`. |

## Open Questions

- Which concrete `IChatClient` provider, model/deployment, endpoint configuration, and credential mechanism is available to the owner? This must be decided before wiring the optional credentialed smoke path; it does not block deterministic implementation or tests.
- Does the owner prefer any visual styling beyond accessible semantic HTML and a small local stylesheet? The default plan is deliberately minimal.

No other architectural decisions are reopened by this plan. A discovered conflict with MAF 1.17.0, a new package outside the specification, or a change to public/persisted contracts must be brought to the owner before implementation proceeds.

## Definition of Done Applied to Every Task

- Task-specific acceptance criteria are met and behavior is exercised at runtime.
- New behavior has focused tests that fail without the change; existing tests remain green.
- Error paths and specified edge cases are covered; no technical defect is converted to a domain outcome.
- Code is scoped, formatted, nullable-clean, async for I/O, and free of debug/dead code.
- Public behavior and material architecture decisions are documented when introduced.
- Security, observability, compatibility, and rollback implications are reviewed in proportion to the task.
- The task is not marked complete until its verification is run or explicitly reported as unrun.
