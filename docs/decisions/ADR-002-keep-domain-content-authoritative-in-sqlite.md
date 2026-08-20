# ADR-002: Keep domain content authoritative in SQLite

## Status

Accepted

## Date

2026-08-20

## Context

MAF filesystem checkpoints and SQLite serve different recovery needs. A checkpoint preserves the complete MAF runtime state needed to rehydrate a run, including execution position, pending messages, executor state, and the pending external-request envelope. SQLite preserves the immutable release, evidence, evaluation, request, snapshot, response, and audit history shown by the application.

The application does not retain a live `StreamingRun` between HTTP requests. SQLite provides the workflow session ID and expected pending-wait identity; each interaction rebuilds the stable graph, selects the latest checkpoint for that session, and asks MAF to rehydrate a new run. This is the application's deliberately uniform durable-continuation lifecycle, not a general MAF requirement. MAF also supports sending a response directly through a retained live run.

The original approval port carried a complete `ApprovalRequest`, including the decision snapshot and brief. After restoration, the decision page rendered that checkpoint copy while response persistence used the active SQLite request. Keeping SQLite authoritative under that design required every business-relevant checkpoint attribute to be compared with its SQLite counterpart. Any field omitted from comparison could become a second, divergent source of presentation or decision data. Remediation restoration behaved differently and recovered only correlation identity.

The stores cannot share a transaction, and access to the local checkpoint directory is not a cryptographic integrity guarantee. The application needs one explicit authority per concern and a small reconciliation boundary.

## Decision

Use the following authority split:

| Concern | Stored by | Purpose |
|---|---|---|
| MAF execution position, messages, executor state, and pending external-request envelope | Checkpoint | Rehydrate the exact workflow continuation. |
| Session ID and expected wait tuple | SQLite workflow correlation | Select the checkpoint stream and state which wait may be resumed. |
| Domain request ID inside the pending MAF request | Checkpointed minimal typed reference | Correlate the restored wait without carrying domain content. |
| Release, evidence, remediation, snapshot, brief, response, and audit content | SQLite domain records | Render and process business facts from one source of truth. |

The session ID is not recovered from the checkpoint. SQLite supplies it to select the checkpoint stream. MAF then restores the complete runtime state; SQLite does not and cannot reconstruct that runner state.

Both MAF external request ports carry a minimal typed reference containing the durable domain request ID. SQLite workflow correlation stores that domain request ID alongside the workflow session ID, MAF request ID, and request kind. The domain request ID has a foreign key to the durable workflow-request table.

Before a restored wait is returned or a response is sent, `CheckpointStoreCoordinator` verifies the typed request/response port contract and exactly compares the restored workflow request ID and durable domain request ID with the SQLite-derived expected identity. Missing, malformed, mismatched, or incompatible continuation fails closed. This closed identity comparison replaces the previous open-ended obligation to compare every duplicated business attribute. Checkpoint content never repairs, replaces, or overrides SQLite domain content.

After successful reconciliation, decision and remediation interactions render their domain objects from SQLite. Forms continue to omit snapshot and domain-request identities; response delivery remains bound to the restored MAF request inside the application.

The pre-release SQLite database and checkpoint directory remain disposable. This schema and checkpoint-contract correction has no compatibility adapter or data migration.

Checkpoint storage remains trusted private runtime infrastructure. MAF deserializes and restores the checkpoint before the application can reconcile its pending request with SQLite. The reconciliation protects the application's domain-authority boundary, but it is not a sandbox, signature check, or reason to load checkpoints from an untrusted or writable-by-others location.

## Alternatives considered

### Keep the full approval snapshot in the checkpoint

Rejected because it duplicates the business record, creates two possible presentation authorities, increases serialized checkpoint surface, and requires field-by-field reconciliation to detect valid divergence.

### Hash or sign the checkpoint-carried snapshot and compare it with SQLite

Rejected because canonical serialization and key management add complexity while SQLite already stores the complete immutable snapshot. A digest would duplicate authority rather than clarify it.

### Reconstruct workflow continuation from SQLite alone

Rejected because SQLite does not contain MAF runner state. Silently starting a new workflow would lose the original continuation and could duplicate workflow effects.

### Retain live runs and bypass checkpoint restoration during normal operation

Rejected for this application because it would require an in-memory run registry, couple workflow lifetime to web-process memory, and create separate same-process and post-restart continuation paths. Rehydrating every HTTP continuation uses one durable path. This is an application lifecycle choice, not an MAF mandate.

### Trust filesystem permissions as the integrity boundary

Rejected as the only application control. Permissions remain required operational hardening, but they do not address accidental corruption, partial writes, incompatible checkpoints, or application defects that create divergent state.

## Consequences

### Positive

- The decision UI and business handlers have one domain source of truth.
- Application-defined checkpoint request payloads contain stable references rather than decision briefs, evidence, or snapshots; the surrounding checkpoint still contains complete MAF runtime state.
- Approval and remediation restoration follow the same exact reconciliation rule.
- Mismatched domain request identities fail before any response enters the workflow.
- The comparison logic lives in one coordinator method rather than separate service and coordinator implementations.

### Negative

- Existing pre-release SQLite databases and checkpoints are incompatible and must be deleted together.
- Loading an interaction requires both stores to be available and mutually consistent.
- The foreign key constrains correlation to an existing durable request but cannot make SQLite and filesystem writes atomic.
- Checkpoint-directory confidentiality and integrity remain operational requirements because identity reconciliation occurs after MAF rehydration.

## Revisit when

Reconsider this decision if MAF gains a checkpoint backend that can participate in the same transaction as business persistence, or if domain content must be available without SQLite. Preserve the single-authority rule even if the storage technologies change.

## References

- [ADR-001: Use process-exclusive filesystem checkpointing](ADR-001-use-process-exclusive-filesystem-checkpointing.md)
- [Microsoft: Workflow checkpoints, rehydration, and security considerations](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints)
- [Microsoft: Human-in-the-loop workflow requests](https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop)
- [Project architecture specification](../../SPEC.md#123-persistence-and-restart-recovery)
- [CheckpointStoreCoordinator](../../src/ReleaseReadinessCoordinator/Workflow/CheckpointStoreCoordinator.cs)
- [WorkflowWaitResolver](../../src/ReleaseReadinessCoordinator/Workflow/WorkflowWaitResolver.cs)
