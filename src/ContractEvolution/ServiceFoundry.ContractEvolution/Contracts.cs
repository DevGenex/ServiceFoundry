using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceFoundry.ContractEvolution;

public readonly record struct ContractVersion
{
    public ContractVersion(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A contract version value is required.", nameof(value))
            : value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ContractIdentity(string FamilyName, ContractVersion Version);

public enum CompatibilityAssessment
{
    Compatible,
    Upgradeable,
    Breaking,
}

public sealed record EvolutionDiagnostic(string Code, string Message, ContractIdentity? Source = null, ContractIdentity? Target = null);

public sealed class ContractEvolutionValidationException : Exception
{
    public ContractEvolutionValidationException(IReadOnlyList<EvolutionDiagnostic> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<EvolutionDiagnostic> Diagnostics { get; }

    private static string BuildMessage(IReadOnlyList<EvolutionDiagnostic> diagnostics)
        => $"Contract evolution registration failed:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics.Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))}";
}

public sealed record ContractUpgradePlan(
    ContractIdentity Source,
    ContractIdentity Target,
    IReadOnlyList<ContractIdentity> Path,
    CompatibilityAssessment Assessment);

public sealed class ContractMappingResult<TTarget>
{
    public ContractMappingResult(TTarget value, ContractUpgradePlan plan)
    {
        Value = value;
        Plan = plan;
    }

    public ContractUpgradePlan Plan { get; }

    public TTarget Value { get; }
}

public interface IContractEvolutionEngine
{
    CompatibilityAssessment Assess(Type contractType, ContractVersion sourceVersion, ContractVersion targetVersion);

    Type GetClrType(Type contractType, ContractVersion version);

    ContractIdentity GetLatestIdentity(Type contractType);

    ContractUpgradePlan ResolvePlan(Type contractType, ContractVersion sourceVersion, ContractVersion targetVersion);

    object Upgrade(Type contractType, object source, ContractVersion sourceVersion, ContractVersion targetVersion);
}

public static class ContractEvolutionEngineExtensions
{
    public static CompatibilityAssessment Assess<TContract>(this IContractEvolutionEngine engine, string sourceVersion, string targetVersion)
        => engine.Assess(typeof(TContract), new ContractVersion(sourceVersion), new ContractVersion(targetVersion));

    public static ContractIdentity GetLatestIdentity<TContract>(this IContractEvolutionEngine engine)
        => engine.GetLatestIdentity(typeof(TContract));

    public static Type GetClrType<TContract>(this IContractEvolutionEngine engine, string version)
        => engine.GetClrType(typeof(TContract), new ContractVersion(version));

    public static ContractMappingResult<TTarget> Upgrade<TContract, TTarget>(
        this IContractEvolutionEngine engine,
        object source,
        string sourceVersion,
        string? targetVersion = null)
    {
        var resolvedTargetVersion = targetVersion is null
            ? engine.GetLatestIdentity(typeof(TContract)).Version
            : new ContractVersion(targetVersion);

        var plan = engine.ResolvePlan(typeof(TContract), new ContractVersion(sourceVersion), resolvedTargetVersion);
        var upgraded = engine.Upgrade(typeof(TContract), source, new ContractVersion(sourceVersion), resolvedTargetVersion);
        return new ContractMappingResult<TTarget>((TTarget)upgraded, plan);
    }
}

public sealed class ContractEvolutionBuilder
{
    private readonly Dictionary<Type, FamilyRegistration> _families = new();

    public ContractEvolutionBuilder ForContract<TContract>(Action<ContractFamilyBuilder<TContract>>? configure = null)
    {
        var builder = new ContractFamilyBuilder<TContract>(GetOrCreateFamily(typeof(TContract)));
        configure?.Invoke(builder);
        return this;
    }

    public ContractFamilyBuilder<TContract> ForContract<TContract>()
        => new(GetOrCreateFamily(typeof(TContract)));

    public IContractEvolutionEngine Build()
        => ContractEvolutionEngine.Build(_families.Values);

    private FamilyRegistration GetOrCreateFamily(Type contractType)
    {
        if (!_families.TryGetValue(contractType, out var registration))
        {
            registration = new FamilyRegistration(contractType, contractType.Name);
            _families.Add(contractType, registration);
        }

        return registration;
    }
}

public sealed class ContractFamilyBuilder<TContract>
{
    private readonly FamilyRegistration _registration;

    internal ContractFamilyBuilder(FamilyRegistration registration)
    {
        _registration = registration;
    }

    public ContractFamilyBuilder<TContract> Latest<TVersion>(string version)
        => Version<TVersion>(version, isLatest: true);

    public ContractFamilyBuilder<TContract> Map<TFrom, TTo>(string fromVersion, string toVersion, Action<ContractMapBuilder<TFrom, TTo>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _registration.EnsureVersion(new ContractVersion(fromVersion), typeof(TFrom), isLatest: false);
        _registration.EnsureVersion(new ContractVersion(toVersion), typeof(TTo), isLatest: false);

        var mapBuilder = new ContractMapBuilder<TFrom, TTo>();
        configure(mapBuilder);
        _registration.Maps.Add(new MapRegistration(
            new ContractVersion(fromVersion),
            new ContractVersion(toVersion),
            typeof(TFrom),
            typeof(TTo),
            mapBuilder.Bindings.ToArray()));

        return this;
    }

    public ContractFamilyBuilder<TContract> Version<TVersion>(string version, bool isLatest = false)
    {
        _registration.EnsureVersion(new ContractVersion(version), typeof(TVersion), isLatest);
        return this;
    }
}

public sealed class ContractMapBuilder<TFrom, TTo>
{
    private readonly List<BindingRegistration> _bindings = new();

    internal IReadOnlyList<BindingRegistration> Bindings => _bindings;

    public ContractMapBuilder<TFrom, TTo> Compute<TValue>(Expression<Func<TTo, TValue>> target, Func<TFrom, TValue> factory)
    {
        _bindings.Add(new BindingRegistration(
            BindingKind.Compute,
            GetPropertyName(target),
            SourcePropertyName: null,
            DefaultValue: null,
            Compute: source => factory((TFrom)source)));
        return this;
    }

    public ContractMapBuilder<TFrom, TTo> Default<TValue>(Expression<Func<TTo, TValue>> target, TValue value)
    {
        _bindings.Add(new BindingRegistration(
            BindingKind.Default,
            GetPropertyName(target),
            SourcePropertyName: null,
            DefaultValue: value,
            Compute: null));
        return this;
    }

    public ContractMapBuilder<TFrom, TTo> Rename<TSourceValue, TTargetValue>(
        Expression<Func<TFrom, TSourceValue>> source,
        Expression<Func<TTo, TTargetValue>> target)
    {
        _bindings.Add(new BindingRegistration(
            BindingKind.Rename,
            GetPropertyName(target),
            GetPropertyName(source),
            DefaultValue: null,
            Compute: null));
        return this;
    }

    private static string GetPropertyName<TValue>(Expression<Func<TValue>> expression)
        => throw new NotSupportedException();

    private static string GetPropertyName<TDeclaring, TValue>(Expression<Func<TDeclaring, TValue>> expression)
    {
        Expression current = expression.Body;
        if (current is UnaryExpression unaryExpression && unaryExpression.NodeType == ExpressionType.Convert)
        {
            current = unaryExpression.Operand;
        }

        if (current is not MemberExpression memberExpression)
        {
            throw new InvalidOperationException("Only direct property access expressions are supported.");
        }

        return memberExpression.Member.Name;
    }
}

public static class ContractEvolutionServiceCollectionExtensions
{
    public static IServiceCollection AddContractEvolution(this IServiceCollection services, Action<ContractEvolutionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ContractEvolutionBuilder();
        configure(builder);
        var engine = builder.Build();
        services.AddSingleton(engine);
        services.AddSingleton<IContractEvolutionEngine>(engine);
        services.AddSingleton<IContractEvolutionReportProvider>(engine.GetReportProvider());
        return services;
    }
}