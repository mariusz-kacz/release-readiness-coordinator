using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ReleaseReadinessCoordinator.Data;

internal static class EntityConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureReleaseRevision(modelBuilder.Entity<ReleaseRevisionRow>());
        ConfigureEvidence(modelBuilder.Entity<EvidenceRecordRow>());
        ConfigureCurrentEvidence(modelBuilder.Entity<CurrentEvidenceRow>());
        ConfigureEvaluationRound(modelBuilder.Entity<EvaluationRoundRow>());
        ConfigureBranchResult(modelBuilder.Entity<BranchResultRow>());
        ConfigureWorkflowRequest(modelBuilder.Entity<WorkflowRequestRow>());
        ConfigureRemediationSubmission(modelBuilder.Entity<RemediationSubmissionRow>());
        ConfigureDecisionSnapshot(modelBuilder.Entity<DecisionSnapshotRow>());
        ConfigureDecisionSnapshotSource(modelBuilder.Entity<DecisionSnapshotSourceRow>());
        ConfigureHumanResponse(modelBuilder.Entity<HumanResponseRow>());
        ConfigureWorkflowCorrelation(modelBuilder.Entity<WorkflowCorrelationRow>());
        ConfigureTimelineEntry(modelBuilder.Entity<TimelineEntryRow>());
    }

    private static void ConfigureReleaseRevision(EntityTypeBuilder<ReleaseRevisionRow> builder)
    {
        builder.ToTable("ReleaseRevisions", table => table.HasCheckConstraint(
            "CK_ReleaseRevisions_DeploymentWindow",
            "RequestedWindowEndUtc >= RequestedWindowStartUtc"));
        builder.HasKey(row => new { row.ReleaseId, row.Revision });
        builder.Property(row => row.ReleaseId).HasMaxLength(200);
        builder.Property(row => row.ServiceName).HasMaxLength(200);
        builder.Property(row => row.ReleaseVersion).HasMaxLength(100);
        builder.Property(row => row.Phase).HasConversion<int>();
        ConfigureConcurrencyToken(builder.Property(row => row.ConcurrencyToken));
        ConfigureOperationKey(builder, "UX_ReleaseRevisions_OperationKey");

        MakeImmutable(builder.Property(row => row.ServiceName));
        MakeImmutable(builder.Property(row => row.ReleaseVersion));
        MakeImmutable(builder.Property(row => row.RequestedWindowStartUtc));
        MakeImmutable(builder.Property(row => row.RequestedWindowEndUtc));
        MakeImmutable(builder.Property(row => row.SubmittedAtUtc));
        MakeImmutable(builder.Property(row => row.OperationKey));
    }

    private static void ConfigureEvidence(EntityTypeBuilder<EvidenceRecordRow> builder)
    {
        builder.ToTable("EvidenceRecords", table => table.HasCheckConstraint(
            "CK_EvidenceRecords_Version",
            "Version > 0"));
        builder.HasKey(row => row.Id);
        builder.Property(row => row.ReleaseId).HasMaxLength(200);
        builder.Property(row => row.Kind).HasConversion<int>();
        builder.HasIndex(row => new { row.ReleaseId, row.Revision, row.Kind, row.Version })
            .IsUnique()
            .HasDatabaseName("UX_EvidenceRecords_Release_Kind_Version");
        builder.HasAlternateKey(row => new { row.ReleaseId, row.Revision, row.Kind, row.Id });
        builder.HasOne<ReleaseRevisionRow>()
            .WithMany()
            .HasForeignKey(row => new { row.ReleaseId, row.Revision })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceRecordRow>()
            .WithMany()
            .HasForeignKey(row => new
            {
                row.ReleaseId,
                row.Revision,
                row.Kind,
                row.SupersedesEvidenceId,
            })
            .HasPrincipalKey(row => new { row.ReleaseId, row.Revision, row.Kind, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureOperationKey(builder, "UX_EvidenceRecords_OperationKey");
        MakeAllPropertiesImmutable(builder);
    }

    private static void ConfigureCurrentEvidence(EntityTypeBuilder<CurrentEvidenceRow> builder)
    {
        builder.ToTable("CurrentEvidence");
        builder.HasKey(row => new { row.ReleaseId, row.Revision, row.Kind });
        builder.Property(row => row.ReleaseId).HasMaxLength(200);
        builder.Property(row => row.Kind).HasConversion<int>();
        builder.HasIndex(row => row.EvidenceId).IsUnique();
        builder.HasOne<ReleaseRevisionRow>()
            .WithMany()
            .HasForeignKey(row => new { row.ReleaseId, row.Revision })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceRecordRow>()
            .WithMany()
            .HasForeignKey(row => new { row.ReleaseId, row.Revision, row.Kind, row.EvidenceId })
            .HasPrincipalKey(row => new { row.ReleaseId, row.Revision, row.Kind, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureConcurrencyToken(builder.Property(row => row.ConcurrencyToken));
    }

    private static void ConfigureEvaluationRound(EntityTypeBuilder<EvaluationRoundRow> builder)
    {
        builder.ToTable("EvaluationRounds", table =>
        {
            table.HasCheckConstraint("CK_EvaluationRounds_Number", "RoundNumber > 0");
            table.HasCheckConstraint(
                "CK_EvaluationRounds_Completion",
                "CompletedAtUtc IS NULL OR CompletedAtUtc >= StartedAtUtc");
        });
        builder.HasKey(row => row.Id);
        builder.Property(row => row.ReleaseId).HasMaxLength(200);
        builder.HasIndex(row => new { row.ReleaseId, row.Revision, row.RoundNumber }).IsUnique();
        builder.HasOne<ReleaseRevisionRow>()
            .WithMany()
            .HasForeignKey(row => new { row.ReleaseId, row.Revision })
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureOperationKey(builder, "UX_EvaluationRounds_OperationKey");
    }

    private static void ConfigureBranchResult(EntityTypeBuilder<BranchResultRow> builder)
    {
        builder.ToTable("BranchResults");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Check).HasConversion<int>();
        builder.Property(row => row.Outcome).HasConversion<int>();
        builder.Property(row => row.Disposition).HasConversion<int>();
        builder.Property(row => row.PlanningReason).HasConversion<int>();
        builder.Property(row => row.EvidenceKind).HasConversion<int>();
        builder.HasIndex(row => new { row.EvaluationRoundId, row.Check })
            .IsUnique()
            .HasDatabaseName("UX_BranchResults_Round_Check");
        builder.HasOne<EvaluationRoundRow>()
            .WithMany()
            .HasForeignKey(row => row.EvaluationRoundId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceRecordRow>()
            .WithMany()
            .HasForeignKey(row => row.EvidenceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BranchResultRow>()
            .WithMany()
            .HasForeignKey(row => row.ReuseSourceResultId)
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureOperationKey(builder, "UX_BranchResults_OperationKey");
        MakeAllPropertiesImmutable(builder);
    }

    private static void ConfigureWorkflowRequest(EntityTypeBuilder<WorkflowRequestRow> builder)
    {
        builder.ToTable("WorkflowRequests", table => table.HasCheckConstraint(
            "CK_WorkflowRequests_Closed",
            "(IsActive = 1 AND ClosedAtUtc IS NULL) OR (IsActive = 0 AND ClosedAtUtc IS NOT NULL)"));
        builder.HasKey(row => row.Id);
        builder.Property(row => row.ReleaseId).HasMaxLength(200);
        builder.Property(row => row.Kind).HasConversion<int>();
        builder.HasIndex(row => new { row.ReleaseId, row.Revision })
            .IsUnique()
            .HasFilter("IsActive = 1")
            .HasDatabaseName("UX_WorkflowRequests_Active_Release");
        builder.HasIndex(row => new { row.EvaluationRoundId, row.Kind })
            .IsUnique()
            .HasDatabaseName("UX_WorkflowRequests_Round_Kind");
        builder.HasOne<ReleaseRevisionRow>()
            .WithMany()
            .HasForeignKey(row => new { row.ReleaseId, row.Revision })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvaluationRoundRow>()
            .WithMany()
            .HasForeignKey(row => row.EvaluationRoundId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DecisionSnapshotRow>()
            .WithMany()
            .HasForeignKey(row => row.DecisionSnapshotId)
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureConcurrencyToken(builder.Property(row => row.ConcurrencyToken));
        ConfigureOperationKey(builder, "UX_WorkflowRequests_OperationKey");
        MakeImmutable(builder.Property(row => row.ReleaseId));
        MakeImmutable(builder.Property(row => row.Revision));
        MakeImmutable(builder.Property(row => row.Kind));
        MakeImmutable(builder.Property(row => row.EvaluationRoundId));
        MakeImmutable(builder.Property(row => row.DecisionSnapshotId));
        MakeImmutable(builder.Property(row => row.CreatedAtUtc));
        MakeImmutable(builder.Property(row => row.OperationKey));
    }

    private static void ConfigureRemediationSubmission(EntityTypeBuilder<RemediationSubmissionRow> builder)
    {
        builder.ToTable("RemediationSubmissions");
        builder.HasKey(row => row.Id);
        builder.HasIndex(row => row.RequestId).IsUnique();
        builder.HasOne<WorkflowRequestRow>()
            .WithMany()
            .HasForeignKey(row => row.RequestId)
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureOperationKey(builder, "UX_RemediationSubmissions_OperationKey");
        MakeAllPropertiesImmutable(builder);
    }

    private static void ConfigureDecisionSnapshot(EntityTypeBuilder<DecisionSnapshotRow> builder)
    {
        builder.ToTable("DecisionSnapshots", table => table.HasCheckConstraint(
            "CK_DecisionSnapshots_RoundNumber",
            "RoundNumber > 0"));
        builder.HasKey(row => row.Id);
        builder.Property(row => row.ReleaseId).HasMaxLength(200);
        builder.HasIndex(row => row.EvaluationRoundId).IsUnique();
        builder.HasOne<ReleaseRevisionRow>()
            .WithMany()
            .HasForeignKey(row => new { row.ReleaseId, row.Revision })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvaluationRoundRow>()
            .WithMany()
            .HasForeignKey(row => row.EvaluationRoundId)
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureConcurrencyToken(builder.Property(row => row.ConcurrencyToken));
        ConfigureOperationKey(builder, "UX_DecisionSnapshots_OperationKey");
        MakeAllPropertiesImmutable(builder);
    }

    private static void ConfigureDecisionSnapshotSource(EntityTypeBuilder<DecisionSnapshotSourceRow> builder)
    {
        builder.ToTable("DecisionSnapshotSources");
        builder.HasKey(row => new { row.DecisionSnapshotId, row.Check });
        builder.Property(row => row.Check).HasConversion<int>();
        builder.HasIndex(row => new { row.DecisionSnapshotId, row.BranchResultId }).IsUnique();
        builder.HasOne<DecisionSnapshotRow>()
            .WithMany()
            .HasForeignKey(row => row.DecisionSnapshotId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BranchResultRow>()
            .WithMany()
            .HasForeignKey(row => row.BranchResultId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceRecordRow>()
            .WithMany()
            .HasForeignKey(row => row.EvidenceId)
            .OnDelete(DeleteBehavior.Restrict);
        MakeAllPropertiesImmutable(builder);
    }

    private static void ConfigureHumanResponse(EntityTypeBuilder<HumanResponseRow> builder)
    {
        builder.ToTable("HumanResponses");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.ReleaseId).HasMaxLength(200);
        builder.Property(row => row.Decision).HasConversion<int>();
        builder.Property(row => row.ValidationState).HasConversion<int>();
        builder.HasIndex(row => row.RequestId).IsUnique();
        builder.HasIndex(row => new { row.ReleaseId, row.Revision })
            .IsUnique()
            .HasFilter("ValidationState = 1")
            .HasDatabaseName("UX_HumanResponses_Terminal_Release");
        builder.HasOne<ReleaseRevisionRow>()
            .WithMany()
            .HasForeignKey(row => new { row.ReleaseId, row.Revision })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkflowRequestRow>()
            .WithMany()
            .HasForeignKey(row => row.RequestId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DecisionSnapshotRow>()
            .WithMany()
            .HasForeignKey(row => row.SnapshotId)
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureOperationKey(builder, "UX_HumanResponses_OperationKey");
        MakeAllPropertiesImmutable(builder);
    }

    private static void ConfigureWorkflowCorrelation(EntityTypeBuilder<WorkflowCorrelationRow> builder)
    {
        builder.ToTable("WorkflowCorrelations");
        builder.HasKey(row => new { row.ReleaseId, row.Revision });
        builder.Property(row => row.ReleaseId).HasMaxLength(200);
        builder.Property(row => row.PendingRequestKind).HasConversion<int>();
        builder.HasIndex(row => row.WorkflowSessionId).IsUnique();
        builder.HasIndex(row => row.PendingWorkflowRequestId).IsUnique();
        builder.HasOne<ReleaseRevisionRow>()
            .WithMany()
            .HasForeignKey(row => new { row.ReleaseId, row.Revision })
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureConcurrencyToken(builder.Property(row => row.ConcurrencyToken));
        ConfigureOperationKey(builder, "UX_WorkflowCorrelations_OperationKey");
    }

    private static void ConfigureTimelineEntry(EntityTypeBuilder<TimelineEntryRow> builder)
    {
        builder.ToTable("TimelineEntries", table => table.HasCheckConstraint(
            "CK_TimelineEntries_Sequence",
            "Sequence > 0"));
        builder.HasKey(row => row.Id);
        builder.Property(row => row.ReleaseId).HasMaxLength(200);
        builder.Property(row => row.Kind).HasConversion<int>();
        builder.HasIndex(row => new { row.ReleaseId, row.Revision, row.Sequence }).IsUnique();
        builder.HasOne<ReleaseRevisionRow>()
            .WithMany()
            .HasForeignKey(row => new { row.ReleaseId, row.Revision })
            .OnDelete(DeleteBehavior.Restrict);
        ConfigureOperationKey(builder, "UX_TimelineEntries_OperationKey");
        MakeAllPropertiesImmutable(builder);
    }

    private static void ConfigureOperationKey<TEntity>(
        EntityTypeBuilder<TEntity> builder,
        string indexName)
        where TEntity : class, IOperationRow
    {
        builder.Property(row => row.OperationKey).HasMaxLength(300);
        builder.HasIndex(row => row.OperationKey).IsUnique().HasDatabaseName(indexName);
    }

    private static void ConfigureConcurrencyToken(PropertyBuilder<string> property) =>
        property.HasMaxLength(100).IsConcurrencyToken().ValueGeneratedNever();

    private static void MakeImmutable<T>(PropertyBuilder<T> property) =>
        property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

    private static void MakeAllPropertiesImmutable<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        foreach (var property in builder.Metadata.GetProperties())
        {
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }
}
