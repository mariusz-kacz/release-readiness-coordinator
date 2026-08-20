using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Data;

namespace ReleaseReadinessCoordinator.Tests.Support;

internal sealed class TemporaryDatabase : IAsyncDisposable
{
    private TemporaryDatabase(string path)
    {
        Path = path;
    }

    private string Path { get; }

    public static async Task<TemporaryDatabase> CreateAsync()
    {
        var database = new TemporaryDatabase(
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"release-readiness-workflow-{Guid.NewGuid():N}.db"));
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        return database;
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path};Pooling=False;Default Timeout=30")
            .Options;
        return new AppDbContext(options);
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(Path);
        return ValueTask.CompletedTask;
    }
}
