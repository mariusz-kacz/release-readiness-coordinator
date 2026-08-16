using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseReadinessCoordinator.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReleaseRevisions",
                columns: table => new
                {
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ReleaseVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RequestedWindowStartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RequestedWindowEndUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DependencyRequirementsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    PhaseChangedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseRevisions", x => new { x.ReleaseId, x.Revision });
                    table.CheckConstraint("CK_ReleaseRevisions_DeploymentWindow", "RequestedWindowEndUtc >= RequestedWindowStartUtc");
                });

            migrationBuilder.CreateTable(
                name: "EvaluationRounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationRounds", x => x.Id);
                    table.CheckConstraint("CK_EvaluationRounds_Completion", "CompletedAtUtc IS NULL OR CompletedAtUtc >= StartedAtUtc");
                    table.CheckConstraint("CK_EvaluationRounds_Number", "RoundNumber > 0");
                    table.ForeignKey(
                        name: "FK_EvaluationRounds_ReleaseRevisions_ReleaseId_Revision",
                        columns: x => new { x.ReleaseId, x.Revision },
                        principalTable: "ReleaseRevisions",
                        principalColumns: new[] { "ReleaseId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SupersedesEvidenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceRecords", x => x.Id);
                    table.UniqueConstraint("AK_EvidenceRecords_ReleaseId_Revision_Kind_Id", x => new { x.ReleaseId, x.Revision, x.Kind, x.Id });
                    table.CheckConstraint("CK_EvidenceRecords_Version", "Version > 0");
                    table.ForeignKey(
                        name: "FK_EvidenceRecords_EvidenceRecords_ReleaseId_Revision_Kind_SupersedesEvidenceId",
                        columns: x => new { x.ReleaseId, x.Revision, x.Kind, x.SupersedesEvidenceId },
                        principalTable: "EvidenceRecords",
                        principalColumns: new[] { "ReleaseId", "Revision", "Kind", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceRecords_ReleaseRevisions_ReleaseId_Revision",
                        columns: x => new { x.ReleaseId, x.Revision },
                        principalTable: "ReleaseRevisions",
                        principalColumns: new[] { "ReleaseId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimelineEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimelineEntries", x => x.Id);
                    table.CheckConstraint("CK_TimelineEntries_Sequence", "Sequence > 0");
                    table.ForeignKey(
                        name: "FK_TimelineEntries_ReleaseRevisions_ReleaseId_Revision",
                        columns: x => new { x.ReleaseId, x.Revision },
                        principalTable: "ReleaseRevisions",
                        principalColumns: new[] { "ReleaseId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowCorrelations",
                columns: table => new
                {
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkflowSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    PendingWorkflowRequestId = table.Column<string>(type: "TEXT", nullable: false),
                    PendingRequestKind = table.Column<int>(type: "INTEGER", nullable: false),
                    CorrelatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowCorrelations", x => new { x.ReleaseId, x.Revision });
                    table.ForeignKey(
                        name: "FK_WorkflowCorrelations_ReleaseRevisions_ReleaseId_Revision",
                        columns: x => new { x.ReleaseId, x.Revision },
                        principalTable: "ReleaseRevisions",
                        principalColumns: new[] { "ReleaseId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DecisionSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluationRoundId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EarliestValidityBoundUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DecisionBrief = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionSnapshots", x => x.Id);
                    table.CheckConstraint("CK_DecisionSnapshots_RoundNumber", "RoundNumber > 0");
                    table.ForeignKey(
                        name: "FK_DecisionSnapshots_EvaluationRounds_EvaluationRoundId",
                        column: x => x.EvaluationRoundId,
                        principalTable: "EvaluationRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DecisionSnapshots_ReleaseRevisions_ReleaseId_Revision",
                        columns: x => new { x.ReleaseId, x.Revision },
                        principalTable: "ReleaseRevisions",
                        principalColumns: new[] { "ReleaseId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BranchResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EvaluationRoundId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Check = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Disposition = table.Column<int>(type: "INTEGER", nullable: false),
                    PlanningReason = table.Column<int>(type: "INTEGER", nullable: false),
                    PlanningDetail = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EvidenceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    PolicyVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ValidUntilUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AttemptsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FindingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReuseSourceResultId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReuseSourceRound = table.Column<int>(type: "INTEGER", nullable: true),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BranchResults_BranchResults_ReuseSourceResultId",
                        column: x => x.ReuseSourceResultId,
                        principalTable: "BranchResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BranchResults_EvaluationRounds_EvaluationRoundId",
                        column: x => x.EvaluationRoundId,
                        principalTable: "EvaluationRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BranchResults_EvidenceRecords_EvidenceId",
                        column: x => x.EvidenceId,
                        principalTable: "EvidenceRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CurrentEvidence",
                columns: table => new
                {
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SelectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrentEvidence", x => new { x.ReleaseId, x.Revision, x.Kind });
                    table.ForeignKey(
                        name: "FK_CurrentEvidence_EvidenceRecords_ReleaseId_Revision_Kind_EvidenceId",
                        columns: x => new { x.ReleaseId, x.Revision, x.Kind, x.EvidenceId },
                        principalTable: "EvidenceRecords",
                        principalColumns: new[] { "ReleaseId", "Revision", "Kind", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CurrentEvidence_ReleaseRevisions_ReleaseId_Revision",
                        columns: x => new { x.ReleaseId, x.Revision },
                        principalTable: "ReleaseRevisions",
                        principalColumns: new[] { "ReleaseId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluationRoundId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DecisionSnapshotId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRequests", x => x.Id);
                    table.CheckConstraint("CK_WorkflowRequests_Closed", "(IsActive = 1 AND ClosedAtUtc IS NULL) OR (IsActive = 0 AND ClosedAtUtc IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkflowRequests_DecisionSnapshots_DecisionSnapshotId",
                        column: x => x.DecisionSnapshotId,
                        principalTable: "DecisionSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowRequests_EvaluationRounds_EvaluationRoundId",
                        column: x => x.EvaluationRoundId,
                        principalTable: "EvaluationRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowRequests_ReleaseRevisions_ReleaseId_Revision",
                        columns: x => new { x.ReleaseId, x.Revision },
                        principalTable: "ReleaseRevisions",
                        principalColumns: new[] { "ReleaseId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DecisionSnapshotSources",
                columns: table => new
                {
                    DecisionSnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Check = table.Column<int>(type: "INTEGER", nullable: false),
                    BranchResultId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionSnapshotSources", x => new { x.DecisionSnapshotId, x.Check });
                    table.ForeignKey(
                        name: "FK_DecisionSnapshotSources_BranchResults_BranchResultId",
                        column: x => x.BranchResultId,
                        principalTable: "BranchResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DecisionSnapshotSources_DecisionSnapshots_DecisionSnapshotId",
                        column: x => x.DecisionSnapshotId,
                        principalTable: "DecisionSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DecisionSnapshotSources_EvidenceRecords_EvidenceId",
                        column: x => x.EvidenceId,
                        principalTable: "EvidenceRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HumanResponses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotConcurrencyToken = table.Column<string>(type: "TEXT", nullable: false),
                    Decision = table.Column<int>(type: "INTEGER", nullable: false),
                    Responder = table.Column<string>(type: "TEXT", nullable: false),
                    RespondedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ValidationState = table.Column<int>(type: "INTEGER", nullable: false),
                    ValidatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeclineReasonsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HumanResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HumanResponses_DecisionSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "DecisionSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HumanResponses_ReleaseRevisions_ReleaseId_Revision",
                        columns: x => new { x.ReleaseId, x.Revision },
                        principalTable: "ReleaseRevisions",
                        principalColumns: new[] { "ReleaseId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HumanResponses_WorkflowRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "WorkflowRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RemediationSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EvidenceUpdatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ExplicitlySelectedChecksJson = table.Column<string>(type: "TEXT", nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemediationSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemediationSubmissions_WorkflowRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "WorkflowRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BranchResults_EvidenceId",
                table: "BranchResults",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_BranchResults_ReuseSourceResultId",
                table: "BranchResults",
                column: "ReuseSourceResultId");

            migrationBuilder.CreateIndex(
                name: "UX_BranchResults_OperationKey",
                table: "BranchResults",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BranchResults_Round_Check",
                table: "BranchResults",
                columns: new[] { "EvaluationRoundId", "Check" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CurrentEvidence_EvidenceId",
                table: "CurrentEvidence",
                column: "EvidenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CurrentEvidence_ReleaseId_Revision_Kind_EvidenceId",
                table: "CurrentEvidence",
                columns: new[] { "ReleaseId", "Revision", "Kind", "EvidenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionSnapshots_EvaluationRoundId",
                table: "DecisionSnapshots",
                column: "EvaluationRoundId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionSnapshots_ReleaseId_Revision",
                table: "DecisionSnapshots",
                columns: new[] { "ReleaseId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "UX_DecisionSnapshots_OperationKey",
                table: "DecisionSnapshots",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionSnapshotSources_BranchResultId",
                table: "DecisionSnapshotSources",
                column: "BranchResultId");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionSnapshotSources_DecisionSnapshotId_BranchResultId",
                table: "DecisionSnapshotSources",
                columns: new[] { "DecisionSnapshotId", "BranchResultId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionSnapshotSources_EvidenceId",
                table: "DecisionSnapshotSources",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationRounds_ReleaseId_Revision_RoundNumber",
                table: "EvaluationRounds",
                columns: new[] { "ReleaseId", "Revision", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_EvaluationRounds_OperationKey",
                table: "EvaluationRounds",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceRecords_ReleaseId_Revision_Kind_SupersedesEvidenceId",
                table: "EvidenceRecords",
                columns: new[] { "ReleaseId", "Revision", "Kind", "SupersedesEvidenceId" });

            migrationBuilder.CreateIndex(
                name: "UX_EvidenceRecords_OperationKey",
                table: "EvidenceRecords",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_EvidenceRecords_Release_Kind_Version",
                table: "EvidenceRecords",
                columns: new[] { "ReleaseId", "Revision", "Kind", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HumanResponses_RequestId",
                table: "HumanResponses",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HumanResponses_SnapshotId",
                table: "HumanResponses",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "UX_HumanResponses_OperationKey",
                table: "HumanResponses",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_HumanResponses_Terminal_Release",
                table: "HumanResponses",
                columns: new[] { "ReleaseId", "Revision" },
                unique: true,
                filter: "ValidationState = 1");

            migrationBuilder.CreateIndex(
                name: "UX_ReleaseRevisions_OperationKey",
                table: "ReleaseRevisions",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemediationSubmissions_RequestId",
                table: "RemediationSubmissions",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RemediationSubmissions_OperationKey",
                table: "RemediationSubmissions",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimelineEntries_ReleaseId_Revision_Sequence",
                table: "TimelineEntries",
                columns: new[] { "ReleaseId", "Revision", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_TimelineEntries_OperationKey",
                table: "TimelineEntries",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCorrelations_PendingWorkflowRequestId",
                table: "WorkflowCorrelations",
                column: "PendingWorkflowRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCorrelations_WorkflowSessionId",
                table: "WorkflowCorrelations",
                column: "WorkflowSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowCorrelations_OperationKey",
                table: "WorkflowCorrelations",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRequests_DecisionSnapshotId",
                table: "WorkflowRequests",
                column: "DecisionSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRequests_EvaluationRoundId",
                table: "WorkflowRequests",
                column: "EvaluationRoundId");

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowRequests_Active_Release",
                table: "WorkflowRequests",
                columns: new[] { "ReleaseId", "Revision" },
                unique: true,
                filter: "IsActive = 1");

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowRequests_OperationKey",
                table: "WorkflowRequests",
                column: "OperationKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CurrentEvidence");

            migrationBuilder.DropTable(
                name: "DecisionSnapshotSources");

            migrationBuilder.DropTable(
                name: "HumanResponses");

            migrationBuilder.DropTable(
                name: "RemediationSubmissions");

            migrationBuilder.DropTable(
                name: "TimelineEntries");

            migrationBuilder.DropTable(
                name: "WorkflowCorrelations");

            migrationBuilder.DropTable(
                name: "BranchResults");

            migrationBuilder.DropTable(
                name: "WorkflowRequests");

            migrationBuilder.DropTable(
                name: "EvidenceRecords");

            migrationBuilder.DropTable(
                name: "DecisionSnapshots");

            migrationBuilder.DropTable(
                name: "EvaluationRounds");

            migrationBuilder.DropTable(
                name: "ReleaseRevisions");
        }
    }
}
