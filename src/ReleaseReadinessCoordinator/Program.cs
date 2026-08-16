using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Workflow;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddRazorPages();
builder.Services.AddSingleton(TimeProvider.System);

var applicationDataDirectory = ResolveApplicationDataPath(
    builder.Environment.ContentRootPath,
    builder.Configuration["ApplicationData:Directory"] ?? "app-data");
Directory.CreateDirectory(applicationDataDirectory);
var databasePath = Path.Combine(applicationDataDirectory, "release-readiness.db");
var checkpointPath = Path.Combine(applicationDataDirectory, "workflow-checkpoints");
var dataProtectionPath = Directory.CreateDirectory(
    Path.Combine(applicationDataDirectory, "data-protection-keys"));

builder.Services.AddDataProtection().PersistKeysToFileSystem(dataProtectionPath);
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<IApplicationDataService, ApplicationDataService>();
builder.Services.AddSingleton(_ => new CheckpointStoreCoordinator(
    Directory.CreateDirectory(checkpointPath)));
builder.Services.AddScoped(serviceProvider => new ReleaseSubmissionApplicationService(
    serviceProvider.GetRequiredService<IApplicationDataService>(),
    serviceProvider.GetRequiredService<CheckpointStoreCoordinator>(),
    serviceProvider.GetRequiredService<TimeProvider>()));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

static string ResolveApplicationDataPath(string contentRootPath, string configuredPath) =>
    Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.GetFullPath(configuredPath, contentRootPath);
