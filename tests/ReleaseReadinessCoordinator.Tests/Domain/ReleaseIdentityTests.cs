using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class ReleaseIdentityTests
{
    [Fact]
    public void Release_id_is_trimmed_and_compares_by_value()
    {
        var releaseId = new ReleaseId("  release-42  ");

        Assert.Equal("release-42", releaseId.Value);
        Assert.Equal(new ReleaseId("release-42"), releaseId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Release_id_rejects_missing_values(string value)
    {
        Assert.Throws<ArgumentException>(() => new ReleaseId(value));
    }
}
