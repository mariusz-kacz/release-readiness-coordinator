using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ReleaseReadinessCoordinator.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    internal DbSet<ReleaseRevisionRow> ReleaseRevisions => Set<ReleaseRevisionRow>();

    internal DbSet<EvidenceRecordRow> EvidenceRecords => Set<EvidenceRecordRow>();

    internal DbSet<CurrentEvidenceRow> CurrentEvidence => Set<CurrentEvidenceRow>();

    internal DbSet<EvaluationRoundRow> EvaluationRounds => Set<EvaluationRoundRow>();

    internal DbSet<BranchResultRow> BranchResults => Set<BranchResultRow>();

    internal DbSet<RollbackAnalysisRow> RollbackAnalyses => Set<RollbackAnalysisRow>();

    internal DbSet<WorkflowRequestRow> WorkflowRequests => Set<WorkflowRequestRow>();

    internal DbSet<RemediationSubmissionRow> RemediationSubmissions => Set<RemediationSubmissionRow>();

    internal DbSet<DecisionSnapshotRow> DecisionSnapshots => Set<DecisionSnapshotRow>();

    internal DbSet<DecisionSnapshotSourceRow> DecisionSnapshotSources => Set<DecisionSnapshotSourceRow>();

    internal DbSet<HumanResponseRow> HumanResponses => Set<HumanResponseRow>();

    internal DbSet<WorkflowCorrelationRow> WorkflowCorrelations => Set<WorkflowCorrelationRow>();

    internal DbSet<TimelineEntryRow> TimelineEntries => Set<TimelineEntryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        EntityConfiguration.Configure(modelBuilder);
}

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=release-readiness.design.db")
            .Options;

        return new AppDbContext(options);
    }
}
