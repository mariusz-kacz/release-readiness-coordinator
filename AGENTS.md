# Repository Guide

## Root commands

```text
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Run the application locally with:

```text
dotnet run --project src/ReleaseReadinessCoordinator/ReleaseReadinessCoordinator.csproj --launch-profile http
```

The HTTP launch profile listens on `http://localhost:5247`.

## Project paths

- Specification: `SPEC.md`
- Application: `src/ReleaseReadinessCoordinator/ReleaseReadinessCoordinator.csproj`
- Tests: `tests/ReleaseReadinessCoordinator.Tests/ReleaseReadinessCoordinator.Tests.csproj`
- Acceptance evidence: `docs/acceptance-matrix.md`
- Default local state: `src/ReleaseReadinessCoordinator/app-data/` (Git-ignored)

Use `ApplicationData__Directory` to isolate local runs. Preserve the same directory when testing restart recovery, and never run multiple application instances against one checkpoint directory.

## Architecture boundary

Keep `src/ReleaseReadinessCoordinator/ReleaseReadinessCoordinator.csproj` as the only deployable application. It is one ASP.NET Core Razor Pages app containing the bounded domain and workflow code. Keep focused automated tests in `tests/ReleaseReadinessCoordinator.Tests/ReleaseReadinessCoordinator.Tests.csproj`. Do not add another service, client, worker, queue, scheduler, broker, event bus, or other deployable component.

UI verification uses the focused Razor/Kestrel test suite and documented manual journeys. Do not add a browser-automation dependency.
