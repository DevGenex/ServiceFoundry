using System.Collections.ObjectModel;

namespace ServiceFoundry.ConfigGuard;

public enum ConfigDiagnosticSeverity
{
    Error = 0,
    Warning = 1,
}

public sealed record ConfigDiagnostic(
    string Code,
    ConfigDiagnosticSeverity Severity,
    string Message,
    string SectionPath,
    string? KeyPath = null,
    string? SourceKeyPath = null);

public sealed class ConfigValidationResult<TOptions> where TOptions : class
{
    public ConfigValidationResult(TOptions options, IEnumerable<ConfigDiagnostic> diagnostics)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Diagnostics = new ReadOnlyCollection<ConfigDiagnostic>(diagnostics
            .OrderBy(static diagnostic => diagnostic.Severity)
            .ThenBy(static diagnostic => diagnostic.KeyPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }

    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == ConfigDiagnosticSeverity.Error);

    public bool HasWarnings => Diagnostics.Any(static diagnostic => diagnostic.Severity == ConfigDiagnosticSeverity.Warning);

    public TOptions Options { get; }
}

public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(string sectionPath, IReadOnlyList<ConfigDiagnostic> diagnostics)
        : base(BuildMessage(sectionPath, diagnostics))
    {
        SectionPath = sectionPath;
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }

    public string SectionPath { get; }

    private static string BuildMessage(string sectionPath, IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        var lines = diagnostics
            .Where(static diagnostic => diagnostic.Severity == ConfigDiagnosticSeverity.Error)
            .Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}");

        return $"Configuration contract for section '{sectionPath}' failed validation:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }
}