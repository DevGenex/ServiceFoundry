using ServiceFoundry.ContractEvolution.Reporting;
using ServiceFoundry.ContractEvolution.Testing;

namespace ServiceFoundry.ContractEvolution.Reporting.Tests;

public sealed class ContractEvolutionReportingTests
{
    [Fact]
    public void Text_report_describes_versions_and_mappings()
    {
        var provider = BuildProvider();

        var report = new ContractEvolutionTextReportWriter().Write(provider);

        Assert.Contains("Contract: OrderContract", report);
        Assert.Contains("Latest: v2", report);
        Assert.Contains("v1 -> v2 [Upgradeable]", report);
    }

    [Fact]
    public void Json_report_contains_contract_family_name()
    {
        var provider = BuildProvider();

        var report = new ContractEvolutionJsonReportWriter().Write(provider);

        Assert.Contains("OrderContract", report);
        Assert.Contains("v2", report);
    }

    private static IContractEvolutionReportProvider BuildProvider()
    {
        var host = ContractEvolutionTestHost.Create(builder =>
        {
            builder.ForContract<OrderContract>(contract =>
            {
                contract.Version<OrderV1>("v1");
                contract.Latest<OrderV2>("v2");
                contract.Map<OrderV1, OrderV2>("v1", "v2", map =>
                {
                    map.Rename(source => source.CustomerName, target => target.BuyerName);
                    map.Default(target => target.Currency, "USD");
                });
            });
        });

        return host.Engine.GetReportProvider();
    }

    public sealed class OrderContract;

    public sealed class OrderV1
    {
        public string CustomerName { get; set; } = string.Empty;
    }

    public sealed class OrderV2
    {
        public string BuyerName { get; set; } = string.Empty;

        public string Currency { get; set; } = string.Empty;
    }
}