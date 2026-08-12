namespace ReleaseReadinessCoordinator.Tests;

public sealed class SolutionBootstrapTests
{
    [Fact]
    public void FocusedTestProjectReferencesWebApplication()
    {
        var webAssembly = typeof(Pages.IndexModel).Assembly;

        Assert.Equal("ReleaseReadinessCoordinator", webAssembly.GetName().Name);
    }
}
