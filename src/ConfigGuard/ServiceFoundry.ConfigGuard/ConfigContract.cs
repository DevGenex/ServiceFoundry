using System.Collections.ObjectModel;
using System.Linq.Expressions;
using Microsoft.Extensions.Configuration;

namespace ServiceFoundry.ConfigGuard;

public sealed record BindingPolicy(bool DetectUnknownKeys)
{
    public static BindingPolicy Relaxed { get; } = new(false);

    public static BindingPolicy Strict { get; } = new(true);
}

public sealed class ValidationRule<TOptions> where TOptions : class, new()
{
    internal ValidationRule(
        Func<TOptions, bool> predicate,
        string message,
        string code,
        string? keyPath,
        Func<TOptions, bool>? condition)
    {
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        Message = string.IsNullOrWhiteSpace(message) ? throw new ArgumentException("Validation message is required.", nameof(message)) : message;
        Code = string.IsNullOrWhiteSpace(code) ? ConfigContract<TOptions>.ValidationFailedCode : code;
        KeyPath = string.IsNullOrWhiteSpace(keyPath) ? null : NormalizePath(keyPath);
        Condition = condition;
    }

    public string Code { get; }

    public string? KeyPath { get; }

    public string Message { get; }

    internal Func<TOptions, bool>? Condition { get; }

    internal Func<TOptions, bool> Predicate { get; }

    internal static string NormalizePath(string path) => path.Replace("__", ":", StringComparison.Ordinal).Trim(':');
}

public static class ConfigContract
{
    public static ConfigContract<TOptions> Create<TOptions>(
        string sectionPath,
        Action<ConfigContractBuilder<TOptions>> configure,
        BindingPolicy? bindingPolicy = null)
        where TOptions : class, new()
    {
        if (string.IsNullOrWhiteSpace(sectionPath))
        {
            throw new ArgumentException("A configuration section path is required.", nameof(sectionPath));
        }

        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ConfigContractBuilder<TOptions>();
        configure(builder);
        return builder.Build(sectionPath, bindingPolicy ?? BindingPolicy.Strict);
    }
}

public sealed class ConfigContract<TOptions> where TOptions : class, new()
{
    internal const string DeprecatedKeyCode = "CFG003";
    internal const string MissingRequiredCode = "CFG001";
    internal const string UnknownKeyCode = "CFG002";
    internal const string ValidationFailedCode = "CFG004";

    private readonly IReadOnlyList<FieldRule<TOptions>> _fields;
    private readonly IReadOnlyList<ValidationRule<TOptions>> _validationRules;

    internal ConfigContract(
        string sectionPath,
        BindingPolicy bindingPolicy,
        IReadOnlyList<FieldRule<TOptions>> fields,
        IReadOnlyList<ValidationRule<TOptions>> validationRules)
    {
        SectionPath = sectionPath;
        BindingPolicy = bindingPolicy;
        _fields = fields;
        _validationRules = validationRules;
    }

    public BindingPolicy BindingPolicy { get; }

    public string SectionPath { get; }

    public ConfigValidationResult<TOptions> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionPath);
        var snapshot = SectionSnapshot.Create(section, SectionPath);
        var diagnostics = new List<ConfigDiagnostic>();

        if (BindingPolicy.DetectUnknownKeys)
        {
            var knownKeys = BindablePathDiscovery.GetLeafPaths(typeof(TOptions));
            foreach (var field in _fields)
            {
                knownKeys.Add(field.CanonicalKey);
                foreach (var alias in field.Aliases)
                {
                    knownKeys.Add(alias.AliasKey);
                }
            }

            foreach (var unknownKey in snapshot.LeafValues.Values.Where(value => !ConfigPathPatternMatcher.IsKnownPath(knownKeys, value.RelativePath)))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    UnknownKeyCode,
                    ConfigDiagnosticSeverity.Error,
                    $"Unknown configuration key '{unknownKey.RelativePath}' was found under section '{SectionPath}'.",
                    SectionPath,
                    unknownKey.RelativePath,
                    unknownKey.FullPath));
            }
        }

        foreach (var field in _fields)
        {
            if (field.CanonicalDeprecationMessage is not null && snapshot.TryGet(field.CanonicalKey, out var canonicalValue))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    DeprecatedKeyCode,
                    ConfigDiagnosticSeverity.Warning,
                    field.CanonicalDeprecationMessage,
                    SectionPath,
                    field.CanonicalKey,
                    canonicalValue.FullPath));
            }

            foreach (var alias in field.Aliases)
            {
                if (alias.DeprecationMessage is not null && snapshot.TryGet(alias.AliasKey, out var aliasValue))
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        DeprecatedKeyCode,
                        ConfigDiagnosticSeverity.Warning,
                        alias.DeprecationMessage,
                        SectionPath,
                        field.CanonicalKey,
                        aliasValue.FullPath));
                }
            }
        }

        var boundConfiguration = BuildBoundConfiguration(section, snapshot);
        var options = new TOptions();
        boundConfiguration.GetSection(SectionPath).Bind(options);

        foreach (var field in _fields)
        {
            var isPresent = snapshot.Contains(field.CanonicalKey) || field.Aliases.Any(alias => snapshot.Contains(alias.AliasKey));

            foreach (var requirement in field.Requirements)
            {
                if (requirement.Condition is not null && !requirement.Condition(options))
                {
                    continue;
                }

                if (!isPresent)
                {
                    diagnostics.Add(new ConfigDiagnostic(
                        MissingRequiredCode,
                        ConfigDiagnosticSeverity.Error,
                        requirement.Message,
                        SectionPath,
                        field.CanonicalKey));
                }
            }
        }

        foreach (var rule in _validationRules)
        {
            if (rule.Condition is not null && !rule.Condition(options))
            {
                continue;
            }

            if (!rule.Predicate(options))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    rule.Code,
                    ConfigDiagnosticSeverity.Error,
                    rule.Message,
                    SectionPath,
                    rule.KeyPath));
            }
        }

        return new ConfigValidationResult<TOptions>(options, diagnostics);
    }

    internal void Apply(IConfiguration configuration, TOptions destination)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(destination);

        var section = configuration.GetSection(SectionPath);
        var snapshot = SectionSnapshot.Create(section, SectionPath);
        var boundConfiguration = BuildBoundConfiguration(section, snapshot);
        boundConfiguration.GetSection(SectionPath).Bind(destination);
    }

    private IConfiguration BuildBoundConfiguration(IConfigurationSection section, SectionSnapshot snapshot)
    {
        var values = section.AsEnumerable(makePathsRelative: false)
            .Where(static pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var field in _fields)
        {
            if (snapshot.Contains(field.CanonicalKey))
            {
                continue;
            }

            var aliasValue = field.Aliases
                .Select(alias => snapshot.TryGet(alias.AliasKey, out var matched) ? matched : null)
                .FirstOrDefault(static matched => matched is not null);

            if (aliasValue is null)
            {
                continue;
            }

            values[$"{SectionPath}:{field.CanonicalKey}"] = aliasValue.Value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}

public sealed class ConfigContractBuilder<TOptions> where TOptions : class, new()
{
    private readonly Dictionary<string, FieldRule<TOptions>> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ValidationRule<TOptions>> _validationRules = new();

    public ConfigContractBuilder<TOptions> Alias<TValue>(string aliasKey, Expression<Func<TOptions, TValue>> selector, string? deprecationMessage = null)
    {
        if (string.IsNullOrWhiteSpace(aliasKey))
        {
            throw new ArgumentException("Alias key is required.", nameof(aliasKey));
        }

        var field = GetOrCreateField(selector);
        var normalizedAlias = NormalizePath(aliasKey);

        if (string.Equals(field.CanonicalKey, normalizedAlias, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Alias '{aliasKey}' matches the canonical key '{field.CanonicalKey}'.");
        }

        if (_fields.Values.Any(existing =>
                string.Equals(existing.CanonicalKey, normalizedAlias, StringComparison.OrdinalIgnoreCase) ||
                existing.Aliases.Any(alias => string.Equals(alias.AliasKey, normalizedAlias, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException($"Alias '{aliasKey}' is already in use by another configuration member.");
        }

        field.Aliases.Add(new AliasRule(normalizedAlias, deprecationMessage));
        return this;
    }

    public ConfigContractBuilder<TOptions> Deprecate<TValue>(Expression<Func<TOptions, TValue>> selector, string message)
    {
        var field = GetOrCreateField(selector);
        field.CanonicalDeprecationMessage = string.IsNullOrWhiteSpace(message)
            ? $"Configuration key '{field.CanonicalKey}' is deprecated."
            : message;
        return this;
    }

    public ConfigContractBuilder<TOptions> Require<TValue>(
        Expression<Func<TOptions, TValue>> selector,
        string? message = null,
        Func<TOptions, bool>? condition = null)
    {
        var field = GetOrCreateField(selector);
        field.Requirements.Add(new ConditionalRequirement<TOptions>(
            condition,
            string.IsNullOrWhiteSpace(message)
                ? $"Configuration key '{field.CanonicalKey}' is required."
                : message));

        return this;
    }

    public ConfigContractBuilder<TOptions> Validate(
        Func<TOptions, bool> predicate,
        string message,
        string? keyPath = null,
        string? code = null,
        Func<TOptions, bool>? condition = null)
    {
        _validationRules.Add(new ValidationRule<TOptions>(predicate, message, code ?? ConfigContract<TOptions>.ValidationFailedCode, keyPath, condition));
        return this;
    }

    public ConditionalConfigContractBuilder<TOptions> When(Func<TOptions, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ConditionalConfigContractBuilder<TOptions>(this, predicate);
    }

    internal ConfigContract<TOptions> Build(string sectionPath, BindingPolicy bindingPolicy)
    {
        return new ConfigContract<TOptions>(
            NormalizePath(sectionPath),
            bindingPolicy,
            new ReadOnlyCollection<FieldRule<TOptions>>(_fields.Values.OrderBy(static field => field.CanonicalKey, StringComparer.OrdinalIgnoreCase).ToArray()),
            new ReadOnlyCollection<ValidationRule<TOptions>>(_validationRules.ToArray()));
    }

    private FieldRule<TOptions> GetOrCreateField<TValue>(Expression<Func<TOptions, TValue>> selector)
    {
        var key = GetPropertyPath(selector);
        if (!_fields.TryGetValue(key, out var field))
        {
            field = new FieldRule<TOptions>(key);
            _fields.Add(key, field);
        }

        return field;
    }

    private static string GetPropertyPath<TValue>(Expression<Func<TOptions, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        Expression current = selector.Body;
        if (current is UnaryExpression unaryExpression && unaryExpression.NodeType == ExpressionType.Convert)
        {
            current = unaryExpression.Operand;
        }

        var segments = new Stack<string>();
        while (current is MemberExpression memberExpression)
        {
            segments.Push(memberExpression.Member.Name);
            current = memberExpression.Expression ?? throw new InvalidOperationException("Only property access expressions are supported.");
        }

        if (current is not ParameterExpression)
        {
            throw new InvalidOperationException("Only property access expressions are supported.");
        }

        return NormalizePath(string.Join(':', segments));
    }

    private static string NormalizePath(string path) => ValidationRule<TOptions>.NormalizePath(path);
}

public sealed class ConditionalConfigContractBuilder<TOptions> where TOptions : class, new()
{
    private readonly Func<TOptions, bool> _condition;
    private readonly ConfigContractBuilder<TOptions> _inner;

    internal ConditionalConfigContractBuilder(ConfigContractBuilder<TOptions> inner, Func<TOptions, bool> condition)
    {
        _inner = inner;
        _condition = condition;
    }

    public ConfigContractBuilder<TOptions> Require<TValue>(Expression<Func<TOptions, TValue>> selector, string? message = null)
        => _inner.Require(selector, message, _condition);

    public ConfigContractBuilder<TOptions> Validate(Func<TOptions, bool> predicate, string message, string? keyPath = null, string? code = null)
        => _inner.Validate(predicate, message, keyPath, code, _condition);
}

internal sealed class AliasRule
{
    public AliasRule(string aliasKey, string? deprecationMessage)
    {
        AliasKey = aliasKey;
        DeprecationMessage = string.IsNullOrWhiteSpace(deprecationMessage)
            ? $"Configuration key '{aliasKey}' is deprecated."
            : deprecationMessage;
    }

    public string AliasKey { get; }

    public string? DeprecationMessage { get; }
}

internal sealed class ConditionalRequirement<TOptions>
{
    public ConditionalRequirement(Func<TOptions, bool>? condition, string message)
    {
        Condition = condition;
        Message = message;
    }

    public Func<TOptions, bool>? Condition { get; }

    public string Message { get; }
}

internal sealed class FieldRule<TOptions>
{
    public FieldRule(string canonicalKey)
    {
        CanonicalKey = canonicalKey;
    }

    public List<AliasRule> Aliases { get; } = new();

    public string CanonicalKey { get; }

    public string? CanonicalDeprecationMessage { get; set; }

    public List<ConditionalRequirement<TOptions>> Requirements { get; } = new();
}

internal sealed class SectionSnapshot
{
    private SectionSnapshot(Dictionary<string, ConfigValue> leafValues)
    {
        LeafValues = leafValues;
    }

    public IReadOnlyDictionary<string, ConfigValue> LeafValues { get; }

    public bool Contains(string relativePath) => LeafValues.ContainsKey(NormalizePath(relativePath));

    public static SectionSnapshot Create(IConfigurationSection section, string sectionPath)
    {
        var values = section.AsEnumerable(makePathsRelative: false)
            .Where(static pair => pair.Value is not null)
            .Select(pair => ConfigValue.Create(pair.Key, pair.Value!, sectionPath))
            .Where(static value => value.RelativePath.Length > 0)
            .ToDictionary(value => value.RelativePath, StringComparer.OrdinalIgnoreCase);

        return new SectionSnapshot(values);
    }

    public bool TryGet(string relativePath, out ConfigValue value)
        => LeafValues.TryGetValue(NormalizePath(relativePath), out value!);

    private static string NormalizePath(string path) => ValidationRule<object>.NormalizePath(path);
}

internal sealed class ConfigValue
{
    private ConfigValue(string fullPath, string relativePath, string value)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Value = value;
    }

    public string FullPath { get; }

    public string RelativePath { get; }

    public string Value { get; }

    public static ConfigValue Create(string fullPath, string value, string sectionPath)
    {
        var prefix = $"{ValidationRule<object>.NormalizePath(sectionPath)}:";
        var normalizedFullPath = ValidationRule<object>.NormalizePath(fullPath);
        var relativePath = normalizedFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedFullPath[prefix.Length..]
            : normalizedFullPath;

        return new ConfigValue(fullPath, relativePath, value);
    }
}

internal static class BindablePathDiscovery
{
    public static HashSet<string> GetLeafPaths(Type type)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Populate(type, prefix: null, paths, new HashSet<Type>());
        return paths;
    }

    private static void Populate(Type type, string? prefix, ISet<string> paths, ISet<Type> visited)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        if (IsLeafType(underlyingType))
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                paths.Add(prefix);
            }

            return;
        }

        if (!visited.Add(underlyingType))
        {
            return;
        }

        foreach (var property in underlyingType.GetProperties())
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var childPrefix = string.IsNullOrWhiteSpace(prefix)
                ? property.Name
                : $"{prefix}:{property.Name}";

            if (TryGetElementType(property.PropertyType, out var elementType))
            {
                paths.Add(childPrefix);
                Populate(elementType, $"{childPrefix}:*", paths, visited);
                continue;
            }

            Populate(property.PropertyType, childPrefix, paths, visited);
        }

        visited.Remove(underlyingType);
    }

    private static bool IsLeafType(Type type)
        => type.IsPrimitive
           || type.IsEnum
           || type == typeof(string)
           || type == typeof(decimal)
           || type == typeof(DateTime)
           || type == typeof(DateTimeOffset)
           || type == typeof(TimeSpan)
           || type == typeof(Guid)
           || type == typeof(Uri);

    private static bool TryGetElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = typeof(string);
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType() ?? typeof(object);
            return true;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            elementType = type.GetGenericArguments()[1];
            return true;
        }

        var enumerableInterface = type
            .GetInterfaces()
            .FirstOrDefault(interfaceType => interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerableInterface is not null)
        {
            elementType = enumerableInterface.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(object);
        return false;
    }
}

internal static class ConfigPathPatternMatcher
{
    public static bool IsKnownPath(IEnumerable<string> knownPaths, string candidatePath)
        => knownPaths.Any(knownPath => Matches(knownPath, candidatePath));

    private static bool Matches(string knownPath, string candidatePath)
    {
        var knownSegments = knownPath.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidateSegments = candidatePath.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (knownSegments.Length != candidateSegments.Length)
        {
            return false;
        }

        for (var index = 0; index < knownSegments.Length; index++)
        {
            if (string.Equals(knownSegments[index], "*", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(knownSegments[index], candidateSegments[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}