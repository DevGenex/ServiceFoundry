using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using ServiceFoundry.ContractEvolution.Analyzers;

namespace ServiceFoundry.ContractEvolution.Analyzers.Tests;

public sealed class MissingTargetBindingAnalyzerTests
{
    [Fact]
    public async Task Analyzer_reports_unbound_target_member()
    {
        const string source = """
using ServiceFoundry.ContractEvolution;

public sealed class Demo
{
    public void Configure(ContractEvolutionBuilder builder)
    {
        builder.ForContract<OrderContract>(contract =>
        {
            contract.Version<OrderV1>("v1");
            contract.Latest<OrderV2>("v2");
            contract.Map<OrderV1, OrderV2>("v1", "v2", map =>
            {
            });
        });
    }
}

public sealed class OrderContract { }
public sealed class OrderV1 { public string Name { get; set; } = string.Empty; }
public sealed class OrderV2 { public string Name { get; set; } = string.Empty; public string Currency { get; set; } = string.Empty; }
""";

        var diagnostics = await GetDiagnosticsAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(MissingTargetBindingAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("Currency", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyzer_allows_target_members_bound_by_copy_and_default()
    {
        const string source = """
using ServiceFoundry.ContractEvolution;

public sealed class Demo
{
    public void Configure(ContractEvolutionBuilder builder)
    {
        builder.ForContract<OrderContract>(contract =>
        {
            contract.Version<OrderV1>("v1");
            contract.Latest<OrderV2>("v2");
            contract.Map<OrderV1, OrderV2>("v1", "v2", map =>
            {
                map.Default(target => target.Currency, "USD");
            });
        });
    }
}

public sealed class OrderContract { }
public sealed class OrderV1 { public string Name { get; set; } = string.Empty; }
public sealed class OrderV2 { public string Name { get; set; } = string.Empty; public string Currency { get; set; } = string.Empty; }
""";

        var diagnostics = await GetDiagnosticsAsync(source);

        Assert.Empty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12));
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(ContractEvolutionBuilder).Assembly.Location))
            .ToArray()
            ?? throw new InvalidOperationException("Trusted platform assemblies were not available for analyzer test compilation.");

        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerTests",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compilationErrors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(compilationErrors.Length == 0, string.Join(Environment.NewLine, compilationErrors.Select(diagnostic => diagnostic.ToString())));

        var analyzer = new MissingTargetBindingAnalyzer();
        return await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer))
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
    }
}