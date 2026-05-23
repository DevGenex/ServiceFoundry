using System.Text;
using System.Text.Json;

namespace ServiceFoundry.ContractEvolution.Reporting;

public sealed class ContractEvolutionTextReportWriter
{
    public string Write(IContractEvolutionReportProvider provider)
        => Write(provider.GetReports());

    public string Write(IEnumerable<ContractFamilyReport> reports)
    {
        var builder = new StringBuilder();
        foreach (var report in reports)
        {
            builder.AppendLine($"Contract: {report.FamilyName}");
            builder.AppendLine($"Latest: {report.LatestVersion.Version.Value}");
            builder.AppendLine("Versions:");
            foreach (var version in report.Versions)
            {
                builder.AppendLine($"- {version.Identity.Version.Value} => {version.ClrTypeName}{(version.IsLatest ? " (latest)" : string.Empty)}");
            }

            builder.AppendLine("Mappings:");
            foreach (var edge in report.Edges)
            {
                builder.AppendLine($"- {edge.Source.Version.Value} -> {edge.Target.Version.Value} [{edge.Assessment}]");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed class ContractEvolutionJsonReportWriter
{
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string Write(IContractEvolutionReportProvider provider)
        => Write(provider.GetReports());

    public string Write(IEnumerable<ContractFamilyReport> reports)
        => JsonSerializer.Serialize(reports, _options);
}