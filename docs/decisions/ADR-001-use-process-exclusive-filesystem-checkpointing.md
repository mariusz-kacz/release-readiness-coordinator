# ADR-001: Use process-exclusive filesystem checkpointing for the demo

## Status

Accepted

## Date

2026-08-17

## Context

The release-readiness coordinator is a single ASP.NET Core Razor Pages application used as a presenter-led demo. An HTTP request starts or resumes a Microsoft Agent Framework (MAF) workflow and runs it until the next external request or terminal result. The demo must survive an application stop and restart on the same machine without introducing another service, worker, queue, scheduler, broker, or event bus.

The application pins `Microsoft.Agents.AI.Workflows` 1.17.0. Business and audit history is stored in SQLite, while MAF continuation state is stored separately. Microsoft documents `FileSystemJsonCheckpointStore` as durable, process-exclusive, and not thread-safe, requiring external synchronization. Microsoft also positions filesystem checkpointing for local, single-machine workflows that need to survive process restarts.

The checkpoint store and SQLite cannot participate in one atomic transaction. Checkpoint state is therefore workflow-continuation truth, while idempotent SQLite records are business-history truth.

## Decision

Use one application-lifetime `FileSystemJsonCheckpointStore` rooted at `app-data/workflow-checkpoints`.

Wrap the store in a singleton `CheckpointStoreCoordinator` and protect every complete workflow start or resume turn with one process-local `SemaphoreSlim(1, 1)`. The gate is acquired before MAF or checkpoint-manager access and released in `finally` after the workflow reaches its next external wait, terminates, or fails.

This deliberately serializes checkpointed workflow turns across all releases in the application process. Do not replace the global gate with per-session locks while using this store, and do not allow multiple application instances to share its checkpoint directory.

Keep SQLite operations outside the checkpoint gate. Use stable operation keys and reconciliation to make replayed business writes harmless instead of attempting a cross-store transaction.

Restrict checkpoint-directory read and write access to the application identity in any hosted environment. MAF restores complete runtime state from this store before application-level reconciliation, so the store is trusted private infrastructure rather than a safe ingestion boundary for arbitrary files. ADR-002 defines the narrower rule that checkpoint state is not authoritative for application domain content.

## Rationale

This is the smallest design that demonstrates durable MAF checkpointing and human-in-the-loop recovery across a same-machine restart. It follows the concurrency contract of the selected store and fits the expected workload of one presenter driving sequential interactions.

The architecture intentionally optimizes for clarity, local setup, and a visible restart demonstration rather than throughput or horizontal availability.

## Consequences

### Positive

- Workflow waits survive an application restart on the same machine.
- The implementation uses a built-in MAF checkpoint store and a small async critical section.
- There is no additional deployable component or infrastructure dependency.
- Start and resume operations cannot concurrently corrupt the non-thread-safe store within the application process.
- The gate does not block an ASP.NET Core thread while callers wait because it uses `WaitAsync`.

### Negative

- Only one checkpointed workflow turn can run at a time; unrelated releases can block each other.
- A slow workflow turn creates head-of-line latency for every other workflow turn.
- The application cannot safely scale horizontally with this store.
- The process-local gate cannot coordinate another process or application instance.
- Checkpoints depend on the availability and durability of the local application-data directory.
- SQLite and checkpoint writes remain non-atomic and require idempotency and reconciliation around failure windows.

## Alternatives considered

### In-memory checkpointing

Rejected because workflow waits would not survive process restart, which is a required demonstration.

### Per-session in-process locks

Rejected while using `FileSystemJsonCheckpointStore`. They would allow different sessions to access a store that Microsoft documents as not thread-safe, and they would not solve cross-process coordination.

### Sharing the filesystem directory between application instances

Rejected because the store is process-exclusive. A shared filesystem does not turn it into a distributed, concurrency-safe checkpoint backend.

### A distributed or database-backed checkpoint store

Deferred. A backend designed for concurrent cross-process access would enable horizontal scaling, but it would add infrastructure and operational complexity that the presenter demo does not require.

### No checkpointing and restart from business history

Rejected because silently starting a new workflow would lose the original MAF continuation and pending request identity, weakening human-response correlation and risking duplicate workflow effects.

## Revisit when

Reconsider this decision when any of the following becomes a real requirement:

- multiple users must progress unrelated releases concurrently;
- checkpoint-gate wait time becomes user-visible or operationally significant;
- the application must run more than one instance or provide high availability;
- workflows must resume on a different machine;
- local application storage is ephemeral or cannot meet durability requirements.

At that point, replace the filesystem store with a checkpoint backend whose documented contract supports concurrent and cross-process access. Preserve stable workflow topology, executor and port identities, session identifiers, and external-request correlation. Reassess whether a per-session concurrency guard is still required, and add migration or explicit incompatibility handling for existing checkpoints.

## References

- [Microsoft: FileSystemJsonCheckpointStore API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.workflows.checkpointing.filesystemjsoncheckpointstore?view=agent-framework-dotnet-latest)
- [Microsoft: Workflow checkpoints](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints)
- [CheckpointStoreCoordinator](../../src/ReleaseReadinessCoordinator/Workflow/CheckpointStoreCoordinator.cs)
- [Application registration and data paths](../../src/ReleaseReadinessCoordinator/Program.cs)
- [Project architecture specification](../../SPEC.md#123-persistence-and-restart-recovery)
- [ADR-002: Keep domain content authoritative in SQLite](ADR-002-keep-domain-content-authoritative-in-sqlite.md)

The Microsoft API reference currently displays an older package label than the application's pinned 1.17.0 package. The repository's compiled checkpoint contract tests remain the version-specific verification of start, restore, pending-request re-emission, request correlation, and incompatible-graph failure behavior.
