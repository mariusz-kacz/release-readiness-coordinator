# Release Readiness Coordinator — Authoritative Project Rules

## 1. Authority and change control

Read this file before planning, implementing, refactoring, or reviewing the project.

Precedence:

1. An explicit instruction from the project owner for the current task.
2. This file.
3. The approved `IMPLEMENTATION_PLAN.md`.
4. Existing code and other documentation.

Do not silently weaken, reinterpret, or modify these rules. A proposed deviation must be identified explicitly, justified, and approved by the project owner.

This file defines stable constraints. Exact classes, database tables, MAF APIs, UI components, and implementation slices belong in `IMPLEMENTATION_PLAN.md`.

## 2. Purpose and portfolio signal

The project is a bounded .NET portfolio MVP demonstrating Microsoft Agent Framework workflow orchestration through a realistic release-readiness process.

The system coordinates evidence produced by simulated external systems. It does not run tests, perform security scans, manage changes, or deploy software.

The completed MVP must visibly demonstrate:

- parallel readiness checks and aggregation;
- expected branch-local partial failures;
- a durable wait for external remediation;
- selective re-execution and safe result reuse;
- checkpoint-based recovery after application restart;
- a final human approval or rejection;
- one narrowly bounded LLM task separated from deterministic policy.

The target shape is one workflow, four readiness checks, one aggregator, one remediation gate, one approval gate, one small LLM-backed analyser, and one basic web UI.

## 3. Required end-to-end demonstration

The principal scenario must support this journey:

1. A release coordinator submits one release candidate.
2. Test, Security, Change, and Dependency checks run as one evaluation round.
3. The workflow forms a complete four-branch result view.
4. If every branch passes, the workflow requests a human release decision.
5. If any branch blocks, lacks evidence, or exhausts a transient retry, the workflow requests remediation after aggregation.
6. The coordinator supplies corrected evidence or release information.
7. A new evaluation round executes only branches that are affected, invalid, expired, or previously unsuccessful.
8. Still-valid successful results are reused without recomputation.
9. Before accepting approval, the system verifies that the passing evaluation snapshot is still current.
10. A workflow waiting for remediation or approval can be restored after the application is stopped and restarted.

The UI and audit history must make each evaluation round understandable, especially which branches were **executed** and which were **reused**.

## 4. Workflow semantics

Each readiness branch must finish promptly with one expected result:

- `Passed`;
- `Blocked`;
- `MissingEvidence`;
- `TransientFailure`.

A readiness branch must not wait for a person or remain alive while evidence is corrected. The workflow may wait only after the current round has aggregated all branch results.

Expected missing evidence, policy blockers, and known transient source failures are domain results. Unexpected programming errors, corrupted workflow state, and unrecoverable persistence failures remain technical failures and should fail the workflow rather than becoming another business status.

MAF must perform the real orchestration. Do not implement the process entirely in an ordinary application service and use MAF as a decorative wrapper.

Use a fixed, understandable workflow topology. Do not generate a new graph dynamically for each round.

The rules do not prescribe one MAF routing API for selective reruns. The implementation plan must choose the smallest reliable option supported by the pinned MAF version, such as:

- sending all four branches an `Execute` or `Reuse` work item; or
- conditionally activating only selected branches and combining their new results with stored valid results.

Whichever option is chosen, every round must produce a complete four-branch view and only selected branches may perform real work.

## 5. Minimal readiness policy

All authoritative readiness decisions are deterministic C# decisions.

The MVP policies should remain small:

- **Test:** evidence matches the release version, meets the pass threshold, has no failed critical suite, and is current.
- **Security:** evidence matches the release version, has no unresolved critical finding, treats high findings through a current approved exception, and is current.
- **Change:** the change is approved, the deployment is within its approved window, and the rollback-plan checklist satisfies the deterministic policy.
- **Dependency:** required versions are compatible, dependencies are available during the release window, no maintenance conflict exists, and evidence is current.

Do not build a configurable policy language, generic rules engine, or user-configurable governance platform.

## 6. Selective rerun and safe reuse

Selective rerun is the central feature and must not be reduced to rerunning every check after remediation.

A previous result may be reused only when it:

- passed;
- has not expired;
- is based on unchanged evidence;
- is based on unchanged relevant release inputs;
- was produced by the current policy and analyser versions;
- has not been explicitly invalidated.

A branch must execute again when its prior result did not pass, its evidence changed or expired, a relevant release input changed, its policy or analyser version changed, or it was explicitly invalidated.

A reused branch must not call its evidence provider, rerun its policy evaluator, or invoke the LLM. The stored result must identify the earlier evaluation round from which it was reused.

The implementation plan must define a small explicit invalidation map. Do not introduce a generic dependency graph.

## 7. Deterministic and LLM authority

The MVP has exactly one LLM-backed capability: rollback-plan checklist analysis.

The analyser may identify evidence in free text for:

- application rollback;
- database rollback;
- configuration rollback;
- verification steps;
- rollback decision owner.

It must return structured item-level findings, cite supporting text when present, and abstain when information is absent or ambiguous. It must not invent missing steps.

Deterministic C# code decides:

- whether the Change check passes;
- all other branch outcomes;
- retries and retry limits;
- expiration and invalidation;
- which branches execute or reuse results;
- aggregation and workflow transitions;
- whether a human decision can be accepted;
- final decision persistence.

Do not add release-metadata extraction, LLM-generated authoritative decision briefs, additional agents, agent handoffs, or agent conversations. The release decision brief must be generated deterministically from structured workflow data.

## 8. Human approval integrity

The final release decision is always made by a human release manager.

Approval or rejection must reference the latest fully passing evaluation snapshot. When a response arrives, deterministically recheck that:

- the release is still waiting for that decision;
- relevant release inputs and evidence have not changed;
- no reused or executed result has expired;
- the decision brief still represents the latest results.

A stale approval must not be accepted. The workflow must return to evaluation and rerun only the affected checks.

Approval and rejection are terminal for the submitted release revision.

## 9. Persistence, retries, and simulated integrations

Use one bounded solution and one deployable application.

Use:

- SQLite for release, evidence, evaluation-round, branch-result, remediation, decision, and timeline data;
- a supported lightweight MAF checkpoint store for workflow continuation;
- local in-process, JSON, or SQLite-backed evidence providers;
- an injectable clock for deterministic freshness and expiration scenarios.

Business persistence and MAF checkpoint persistence must remain conceptually separate.

Retry only known transient evidence-source failures. Use a small bounded number of immediate attempts per evaluation round. Do not add durable timers, background retry workers, queues, schedulers, or exponential-backoff infrastructure.

The MVP must include a demonstrable restart-and-resume scenario while waiting for remediation or approval.

Duplicate submission protection may be implemented with a unique release identifier and a simple conflict rule. Do not build a general idempotency platform.

## 10. Scope, UI, and verification constraints

Keep complexity in workflow semantics rather than infrastructure.

Do not add:

- real enterprise integrations;
- distributed services, containers-as-architecture, message brokers, or event buses;
- event sourcing, CQRS infrastructure, generic repositories, or plugin systems;
- a generic workflow designer or dynamic policy engine;
- complex authentication or authorization;
- multi-agent patterns;
- deployment execution, notifications, analytics, or predictive risk scoring;
- reopening rejected releases;
- cancellation unless the core MVP is complete and the owner explicitly adds it.

The UI needs only to show submission details, current workflow state, four branch results, evidence and findings, evaluation rounds, remediation, executed-versus-reused status, the deterministic decision brief, timeline, and human decision controls.

Testing must be proportional to risk. Prefer data-driven deterministic tests, a small set of end-to-end workflow scenarios, a small curated rollback-analysis dataset, one restart/resume test, and minimal UI smoke coverage. Do not target a test count or test trivial wiring and data containers.

Every planning or implementation review must validate at least these questions:

1. Is MAF performing real orchestration?
2. Does waiting happen only after fan-in?
3. Are expected branch outcomes limited and technical failures kept technical?
4. Is there still exactly one LLM capability?
5. Are reused results demonstrably safe and not recomputed?
6. Can stale approval be rejected and routed back to evaluation?
7. Has any unnecessary platform, abstraction, document, or test category entered the MVP?
