namespace ServiceFoundry.ContractEvolution.Testing;

public sealed class ContractEvolutionTestHost
{
    private ContractEvolutionTestHost(IContractEvolutionEngine engine)
    {
        Engine = engine;
    }

    public IContractEvolutionEngine Engine { get; }

    public static ContractEvolutionTestHost Create(Action<ContractEvolutionBuilder> configure)
    {
        var builder = new ContractEvolutionBuilder();
        configure(builder);
        return new ContractEvolutionTestHost(builder.Build());
    }
}

public static class ContractEvolutionAssertions
{
    public static void AssertAssessment(CompatibilityAssessment expected, ContractUpgradePlan plan)
    {
        if (plan.Assessment != expected)
        {
            throw new InvalidOperationException($"Expected assessment '{expected}' but found '{plan.Assessment}'.");
        }
    }

    public static void AssertPath(ContractUpgradePlan plan, params string[] versions)
    {
        var actual = plan.Path.Select(identity => identity.Version.Value).ToArray();
        if (!actual.SequenceEqual(versions, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected path '{string.Join(" -> ", versions)}' but found '{string.Join(" -> ", actual)}'.");
        }
    }
}