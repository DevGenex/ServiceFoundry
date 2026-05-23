using System.Text;
using Microsoft.AspNetCore.Http;
using ServiceFoundry.ContractEvolution.AspNetCore;

namespace ServiceFoundry.ContractEvolution.AspNetCore.Tests;

public sealed class AspNetCoreIntegrationTests
{
    [Fact]
    public void Header_reader_reads_requested_version()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Contract-Version"] = "v1";

        var reader = new HeaderContractVersionReader();

        var version = reader.ReadVersion(context.Request);
        Assert.Equal("v1", version?.Value);
    }

    [Fact]
    public async Task Request_upgrader_reads_legacy_json_and_upgrades_to_latest_contract()
    {
        var engine = new ContractEvolutionBuilder()
            .ForContract<OrderRequestContract>(contract =>
            {
                contract.Version<OrderRequestV1>("v1");
                contract.Latest<OrderRequestV2>("v2");
                contract.Map<OrderRequestV1, OrderRequestV2>("v1", "v2", map =>
                {
                    map.Rename(source => source.CustomerName, target => target.BuyerName);
                    map.Default(target => target.Currency, "USD");
                });
            })
            .Build();

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Contract-Version"] = "v1";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"customerName\":\"Ada\",\"amount\":42}"));

        var upgrader = new HttpRequestContractUpgrader(
            engine,
            new HeaderContractVersionReader(),
            new ContractEvolutionAspNetCoreOptions());

        var upgraded = await upgrader.ReadAndUpgradeAsync<OrderRequestContract, OrderRequestV2>(context.Request);

        Assert.Equal("Ada", upgraded.BuyerName);
        Assert.Equal("USD", upgraded.Currency);
        Assert.Equal(42m, upgraded.Amount);
    }

    public sealed class OrderRequestContract;

    public sealed class OrderRequestV1
    {
        public decimal Amount { get; set; }

        public string CustomerName { get; set; } = string.Empty;
    }

    public sealed class OrderRequestV2
    {
        public decimal Amount { get; set; }

        public string BuyerName { get; set; } = string.Empty;

        public string Currency { get; set; } = string.Empty;
    }
}