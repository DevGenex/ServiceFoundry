using ServiceFoundry.ContractEvolution.Testing;

namespace ServiceFoundry.ContractEvolution.Tests;

public sealed class ContractEvolutionTests
{
    [Fact]
    public void Upgrade_resolves_multi_hop_path_and_marks_it_upgradeable()
    {
        var host = ContractEvolutionTestHost.Create(builder =>
        {
            builder.ForContract<OrderCreatedContract>(contract =>
            {
                contract.Version<OrderCreatedV1>("v1");
                contract.Version<OrderCreatedV2>("v2");
                contract.Latest<OrderCreatedV3>("v3");
                contract.Map<OrderCreatedV1, OrderCreatedV2>("v1", "v2", map =>
                {
                    map.Rename(source => source.CustomerName, target => target.BuyerName);
                    map.Default(target => target.Currency, "USD");
                });
                contract.Map<OrderCreatedV2, OrderCreatedV3>("v2", "v3", map =>
                {
                    map.Compute(target => target.Total, source => $"{source.Amount}:{source.Currency}");
                });
            });
        });

        var result = host.Engine.Upgrade<OrderCreatedContract, OrderCreatedV3>(new OrderCreatedV1 { CustomerName = "Ada", Amount = 42m }, "v1");

        ContractEvolutionAssertions.AssertAssessment(CompatibilityAssessment.Upgradeable, result.Plan);
        ContractEvolutionAssertions.AssertPath(result.Plan, "v1", "v2", "v3");
        Assert.Equal("Ada", result.Value.BuyerName);
        Assert.Equal("42:USD", result.Value.Total);
    }

    [Fact]
    public void Registration_fails_when_target_members_are_unbound()
    {
        var exception = Assert.Throws<ContractEvolutionValidationException>(() => ContractEvolutionTestHost.Create(builder =>
        {
            builder.ForContract<OrderCreatedContract>(contract =>
            {
                contract.Version<OrderCreatedV1>("v1");
                contract.Latest<OrderCreatedBrokenV2>("v2");
                contract.Map<OrderCreatedV1, OrderCreatedBrokenV2>("v1", "v2", _ => { });
            });
        }));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "EVO004");
    }

    [Fact]
    public void Registration_fails_on_ambiguous_shortest_paths()
    {
        var exception = Assert.Throws<ContractEvolutionValidationException>(() => ContractEvolutionTestHost.Create(builder =>
        {
            builder.ForContract<OrderCreatedContract>(contract =>
            {
                contract.Version<ContractV1>("v1");
                contract.Version<ContractV2A>("v2a");
                contract.Version<ContractV2B>("v2b");
                contract.Latest<ContractV3>("v3");
                contract.Map<ContractV1, ContractV2A>("v1", "v2a", _ => { });
                contract.Map<ContractV1, ContractV2B>("v1", "v2b", _ => { });
                contract.Map<ContractV2A, ContractV3>("v2a", "v3", _ => { });
                contract.Map<ContractV2B, ContractV3>("v2b", "v3", _ => { });
            });
        }));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "EVO005");
    }

    [Fact]
    public void Assess_returns_breaking_when_no_upgrade_path_exists()
    {
        var host = ContractEvolutionTestHost.Create(builder =>
        {
            builder.ForContract<OrderCreatedContract>(contract =>
            {
                contract.Version<OrderCreatedV1>("v1");
                contract.Latest<OrderCreatedV2>("v2");
                contract.Map<OrderCreatedV1, OrderCreatedV2>("v1", "v2", map =>
                {
                    map.Rename(source => source.CustomerName, target => target.BuyerName);
                    map.Default(target => target.Currency, "USD");
                });
            });
        });

        var assessment = host.Engine.Assess<OrderCreatedContract>("v2", "v1");
        Assert.Equal(CompatibilityAssessment.Breaking, assessment);
    }

    public sealed class OrderCreatedContract;

    public sealed class OrderCreatedV1
    {
        public decimal Amount { get; set; }

        public string CustomerName { get; set; } = string.Empty;
    }

    public sealed class OrderCreatedV2
    {
        public decimal Amount { get; set; }

        public string BuyerName { get; set; } = string.Empty;

        public string Currency { get; set; } = string.Empty;
    }

    public sealed class OrderCreatedV3
    {
        public string BuyerName { get; set; } = string.Empty;

        public string Total { get; set; } = string.Empty;
    }

    public sealed class OrderCreatedBrokenV2
    {
        public string BuyerName { get; set; } = string.Empty;

        public string Currency { get; set; } = string.Empty;

        public string Region { get; set; } = string.Empty;
    }

    public sealed class ContractV1
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class ContractV2A
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class ContractV2B
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class ContractV3
    {
        public string Name { get; set; } = string.Empty;
    }
}