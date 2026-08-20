using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class Task27ContractTests
{
    [Fact]
    public void Generic_result_and_snapshot_validity_contracts_do_not_exist()
    {
        Assert.Null(typeof(BranchResult).GetProperty("ValidUntil"));
        Assert.Null(typeof(DecisionSnapshot).GetProperty("EarliestValidityBound"));
        Assert.Null(typeof(BranchResult).Assembly.GetType(
            "ReleaseReadinessCoordinator.Domain.FreshnessDeadlines"));
    }

    [Fact]
    public void Planning_reasons_describe_unchanged_evidence_without_expiration()
    {
        var names = Enum.GetNames<PlanningReason>();

        Assert.Contains("UnchangedEvidence", names);
        Assert.DoesNotContain("StillCurrent", names);
        Assert.DoesNotContain("Expired", names);
    }

    [Theory]
    [InlineData(typeof(RoundPlanner))]
    [InlineData(typeof(ResultReuse))]
    [InlineData(typeof(TestReadinessPolicy))]
    [InlineData(typeof(SecurityReadinessPolicy))]
    public void Reuse_and_policy_types_do_not_take_a_clock(Type type)
    {
        Assert.DoesNotContain(
            type.GetConstructors(),
            constructor => constructor.GetParameters().Any(
                parameter => parameter.ParameterType == typeof(TimeProvider)));
    }
}
