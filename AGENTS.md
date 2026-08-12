# Repository Guide

## Root commands

```text
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

## Architecture boundary

Keep `src/ReleaseReadinessCoordinator/ReleaseReadinessCoordinator.csproj` as the only deployable application. It is one ASP.NET Core Razor Pages app containing the bounded domain and workflow code. Keep focused automated tests in `tests/ReleaseReadinessCoordinator.Tests/ReleaseReadinessCoordinator.Tests.csproj`. Do not add another service, client, worker, queue, scheduler, broker, event bus, or other deployable component.
