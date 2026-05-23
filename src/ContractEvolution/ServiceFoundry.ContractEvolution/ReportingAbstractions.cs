namespace ServiceFoundry.ContractEvolution;

public interface IContractEvolutionReportProvider
{
    ContractFamilyReport GetReport(Type contractType);

    IReadOnlyList<ContractFamilyReport> GetReports();
}

public sealed record ContractFamilyReport(
    string FamilyName,
    ContractIdentity LatestVersion,
    IReadOnlyList<ContractVersionReport> Versions,
    IReadOnlyList<ContractEdgeReport> Edges);

public sealed record ContractVersionReport(ContractIdentity Identity, string ClrTypeName, bool IsLatest);

public sealed record ContractEdgeReport(ContractIdentity Source, ContractIdentity Target, CompatibilityAssessment Assessment);

public static class ContractEvolutionReportProviderExtensions
{
    public static ContractFamilyReport GetReport<TContract>(this IContractEvolutionReportProvider provider)
        => provider.GetReport(typeof(TContract));

    public static IContractEvolutionReportProvider GetReportProvider(this IContractEvolutionEngine engine)
        => engine as IContractEvolutionReportProvider
           ?? throw new InvalidOperationException("The configured contract evolution engine does not expose reporting.");
}