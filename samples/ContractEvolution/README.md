# ContractEvolution Sample

```csharp
services.AddContractEvolution(evolution =>
{
    evolution.ForContract<OrderCreatedContract>(contract =>
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
            map.Compute(target => target.Total, source => new Money(source.Amount, source.Currency));
        });
    });
});

var reportProvider = services.BuildServiceProvider().GetRequiredService<IContractEvolutionReportProvider>();
var textReport = new ContractEvolutionTextReportWriter().Write(reportProvider);
```