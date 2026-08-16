using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Data;

public sealed class DatabaseSchemaTests
{
    [Fact]
    public void Model_contains_every_durable_business_and_audit_store()
    {
        using var context = CreateContext("Data Source=:memory:");

        var tables = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(table => table is not null)
            .Select(table => table!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Subset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ReleaseRevisions",
                "EvidenceRecords",
                "CurrentEvidence",
                "EvaluationRounds",
                "BranchResults",
                "WorkflowRequests",
                "RemediationSubmissions",
                "DecisionSnapshots",
                "DecisionSnapshotSources",
                "HumanResponses",
                "WorkflowCorrelations",
                "TimelineEntries",
            },
            tables);
    }

    [Fact]
    public void Model_enforces_identity_history_and_active_record_constraints()
    {
        using var context = CreateContext("Data Source=:memory:");

        var release = context.Model.FindEntityType(typeof(ReleaseRevisionRow))!;
        Assert.Equal(
            [nameof(ReleaseRevisionRow.ReleaseId), nameof(ReleaseRevisionRow.Revision)],
            release.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            PropertySaveBehavior.Throw,
            release.FindProperty(nameof(ReleaseRevisionRow.ServiceName))!.GetAfterSaveBehavior());
        Assert.Equal(
            PropertySaveBehavior.Throw,
            release.FindProperty(nameof(ReleaseRevisionRow.ReleaseVersion))!.GetAfterSaveBehavior());

        AssertUniqueIndex<EvidenceRecordRow>(
            context,
            nameof(EvidenceRecordRow.ReleaseId),
            nameof(EvidenceRecordRow.Revision),
            nameof(EvidenceRecordRow.Kind),
            nameof(EvidenceRecordRow.Version));
        AssertUniqueIndex<BranchResultRow>(
            context,
            nameof(BranchResultRow.EvaluationRoundId),
            nameof(BranchResultRow.Check));
        var activeRequestIndex = AssertUniqueIndex<WorkflowRequestRow>(
            context,
            nameof(WorkflowRequestRow.ReleaseId),
            nameof(WorkflowRequestRow.Revision));
        Assert.Equal("IsActive = 1", activeRequestIndex.GetFilter());
        var terminalResponseIndex = AssertUniqueIndex<HumanResponseRow>(
            context,
            nameof(HumanResponseRow.ReleaseId),
            nameof(HumanResponseRow.Revision));
        Assert.Equal("ValidationState = 1", terminalResponseIndex.GetFilter());

        var currentEvidence = context.Model.FindEntityType(typeof(CurrentEvidenceRow))!;
        Assert.Equal(
            [
                nameof(CurrentEvidenceRow.ReleaseId),
                nameof(CurrentEvidenceRow.Revision),
                nameof(CurrentEvidenceRow.Kind),
            ],
            currentEvidence.FindPrimaryKey()!.Properties.Select(property => property.Name));

        Assert.True(
            context.Model.FindEntityType(typeof(WorkflowRequestRow))!
                .FindProperty(nameof(WorkflowRequestRow.ConcurrencyToken))!
                .IsConcurrencyToken);
        Assert.True(
            context.Model.FindEntityType(typeof(DecisionSnapshotRow))!
                .FindProperty(nameof(DecisionSnapshotRow.ConcurrencyToken))!
                .IsConcurrencyToken);
        Assert.Equal(
            PropertySaveBehavior.Throw,
            context.Model.FindEntityType(typeof(EvidenceRecordRow))!
                .FindProperty(nameof(EvidenceRecordRow.PayloadJson))!
                .GetAfterSaveBehavior());
        Assert.Equal(
            PropertySaveBehavior.Throw,
            context.Model.FindEntityType(typeof(DecisionSnapshotRow))!
                .FindProperty(nameof(DecisionSnapshotRow.DecisionBrief))!
                .GetAfterSaveBehavior());
        Assert.Equal(
            PropertySaveBehavior.Throw,
            context.Model.FindEntityType(typeof(TimelineEntryRow))!
                .FindProperty(nameof(TimelineEntryRow.Summary))!
                .GetAfterSaveBehavior());

        foreach (var entityType in context.Model.GetEntityTypes()
                     .Where(entity => entity.FindProperty("OperationKey") is not null))
        {
            Assert.Contains(
                entityType.GetIndexes(),
                index => index.IsUnique
                    && index.Properties.Select(property => property.Name)
                        .SequenceEqual(["OperationKey"]));
        }
    }

    [Fact]
    public async Task Initial_migration_creates_expected_tables_and_indexes_in_fresh_sqlite_database()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"release-readiness-{Guid.NewGuid():N}.db");

        try
        {
            await using (var context = CreateContext($"Data Source={databasePath};Pooling=False"))
            {
                await context.Database.MigrateAsync();
            }

            await using (var verification = CreateContext($"Data Source={databasePath};Pooling=False"))
            {
                await verification.Database.OpenConnectionAsync();

                var tables = await ReadSchemaObjectNames(verification, "table");
                Assert.Contains("ReleaseRevisions", tables);
                Assert.Contains("EvidenceRecords", tables);
                Assert.Contains("BranchResults", tables);
                Assert.Contains("DecisionSnapshots", tables);
                Assert.Contains("TimelineEntries", tables);

                var indexes = await ReadSchemaObjectNames(verification, "index");
                Assert.Contains("UX_EvidenceRecords_Release_Kind_Version", indexes);
                Assert.Contains("UX_BranchResults_Round_Check", indexes);
                Assert.Contains("UX_WorkflowRequests_Active_Release", indexes);
                Assert.Contains("UX_HumanResponses_Terminal_Release", indexes);
            }
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Active_request_uses_optimistic_concurrency_in_sqlite()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"release-readiness-{Guid.NewGuid():N}.db");
        var releaseId = $"release-{Guid.NewGuid():N}";
        var requestId = Guid.NewGuid();

        try
        {
            await using (var setup = CreateContext($"Data Source={databasePath};Pooling=False"))
            {
                await setup.Database.MigrateAsync();
                setup.ReleaseRevisions.Add(new ReleaseRevisionRow
                {
                    ReleaseId = releaseId,
                    Revision = 1,
                    ServiceName = "orders",
                    ReleaseVersion = "1.0.0",
                    RequestedWindowStartUtc = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero),
                    RequestedWindowEndUtc = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero),
                    DependencyRequirementsJson = "{}",
                    SubmittedAtUtc = new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero),
                    Phase = ProcessPhase.WaitingForRemediation,
                    PhaseChangedAtUtc = new DateTimeOffset(2026, 8, 16, 8, 1, 0, TimeSpan.Zero),
                    OperationKey = $"submit:{releaseId}:1",
                    ConcurrencyToken = "release-token-1",
                });
                setup.WorkflowRequests.Add(new WorkflowRequestRow
                {
                    Id = requestId,
                    ReleaseId = releaseId,
                    Revision = 1,
                    Kind = WorkflowRequestKind.Remediation,
                    CreatedAtUtc = new DateTimeOffset(2026, 8, 16, 8, 1, 0, TimeSpan.Zero),
                    IsActive = true,
                    ConcurrencyToken = "request-token-1",
                    OperationKey = $"request:{requestId:N}",
                });
                await setup.SaveChangesAsync();
            }

            await using (var firstContext = CreateContext($"Data Source={databasePath};Pooling=False"))
            await using (var staleContext = CreateContext($"Data Source={databasePath};Pooling=False"))
            {
                var first = await firstContext.WorkflowRequests.SingleAsync();
                var stale = await staleContext.WorkflowRequests.SingleAsync();

                first.IsActive = false;
                first.ClosedAtUtc = new DateTimeOffset(2026, 8, 16, 8, 2, 0, TimeSpan.Zero);
                first.ConcurrencyToken = "request-token-2";
                await firstContext.SaveChangesAsync();

                stale.IsActive = false;
                stale.ClosedAtUtc = new DateTimeOffset(2026, 8, 16, 8, 3, 0, TimeSpan.Zero);
                stale.ConcurrencyToken = "request-token-stale";

                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                    () => staleContext.SaveChangesAsync());
            }
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static AppDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new AppDbContext(options);
    }

    private static IReadOnlyIndex AssertUniqueIndex<TEntity>(
        DbContext context,
        params string[] propertyNames)
    {
        var entity = context.Model.FindEntityType(typeof(TEntity))!;
        return Assert.Single(
            entity.GetIndexes(),
            index => index.IsUnique
                && index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }

    private static async Task<HashSet<string>> ReadSchemaObjectNames(
        DbContext context,
        string objectType)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$type";
        parameter.Value = objectType;
        command.Parameters.Add(parameter);

        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
