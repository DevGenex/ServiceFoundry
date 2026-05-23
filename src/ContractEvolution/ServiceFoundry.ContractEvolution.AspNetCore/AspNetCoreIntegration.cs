using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceFoundry.ContractEvolution.AspNetCore;

public interface IContractVersionReader
{
    ContractVersion? ReadVersion(HttpRequest request);
}

public sealed class HeaderContractVersionReader : IContractVersionReader
{
    private readonly string _headerName;

    public HeaderContractVersionReader(string headerName = "X-Contract-Version")
    {
        _headerName = headerName;
    }

    public ContractVersion? ReadVersion(HttpRequest request)
    {
        var value = request.Headers.TryGetValue(_headerName, out var values)
            ? values.FirstOrDefault()
            : null;

        if (!string.IsNullOrWhiteSpace(value))
        {
            return new ContractVersion(value);
        }

        return null;
    }
}

public sealed class QueryStringContractVersionReader : IContractVersionReader
{
    private readonly string _key;

    public QueryStringContractVersionReader(string key = "contractVersion")
    {
        _key = key;
    }

    public ContractVersion? ReadVersion(HttpRequest request)
    {
        var value = request.Query.TryGetValue(_key, out var values)
            ? values.FirstOrDefault()
            : null;

        if (!string.IsNullOrWhiteSpace(value))
        {
            return new ContractVersion(value);
        }

        return null;
    }
}

public sealed class CompositeContractVersionReader : IContractVersionReader
{
    private readonly IReadOnlyList<IContractVersionReader> _readers;

    public CompositeContractVersionReader(params IContractVersionReader[] readers)
    {
        _readers = readers;
    }

    public ContractVersion? ReadVersion(HttpRequest request)
    {
        foreach (var reader in _readers)
        {
            var version = reader.ReadVersion(request);
            if (version is not null)
            {
                return version;
            }
        }

        return null;
    }
}

public sealed record ContractVersionMetadata(Type ContractType, ContractVersion Version);

public sealed class ContractEvolutionAspNetCoreOptions
{
    public string HeaderName { get; set; } = "X-Contract-Version";

    public JsonSerializerOptions JsonSerializerOptions { get; } = new(JsonSerializerDefaults.Web);

    public string QueryStringKey { get; set; } = "contractVersion";
}

public static class ContractEvolutionAspNetCoreServiceCollectionExtensions
{
    public static IServiceCollection AddContractEvolutionAspNetCore(
        this IServiceCollection services,
        Action<ContractEvolutionAspNetCoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ContractEvolutionAspNetCoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IContractVersionReader>(_ => new CompositeContractVersionReader(
            new HeaderContractVersionReader(options.HeaderName),
            new QueryStringContractVersionReader(options.QueryStringKey)));
        services.AddSingleton<HttpRequestContractUpgrader>();

        return services;
    }
}

public static class EndpointConventionBuilderExtensions
{
    public static TBuilder WithContractVersion<TBuilder, TContract>(this TBuilder builder, string version)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(new ContractVersionMetadata(typeof(TContract), new ContractVersion(version))));
        return builder;
    }
}

public sealed class HttpRequestContractUpgrader
{
    private readonly IContractEvolutionEngine _engine;
    private readonly ContractEvolutionAspNetCoreOptions _options;
    private readonly IContractVersionReader _versionReader;

    public HttpRequestContractUpgrader(
        IContractEvolutionEngine engine,
        IContractVersionReader versionReader,
        ContractEvolutionAspNetCoreOptions options)
    {
        _engine = engine;
        _versionReader = versionReader;
        _options = options;
    }

    public async Task<TTarget> ReadAndUpgradeAsync<TContract, TTarget>(
        HttpRequest request,
        string? targetVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceVersion = _versionReader.ReadVersion(request) ?? _engine.GetLatestIdentity(typeof(TContract)).Version;
        var resolvedTargetVersion = targetVersion is null
            ? _engine.GetLatestIdentity(typeof(TContract)).Version
            : new ContractVersion(targetVersion);
        var sourceType = _engine.GetClrType(typeof(TContract), sourceVersion);

        var source = await JsonSerializer.DeserializeAsync(request.Body, sourceType, _options.JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            throw new InvalidOperationException("Request body could not be deserialized for contract evolution.");
        }

        return (TTarget)_engine.Upgrade(typeof(TContract), source, sourceVersion, resolvedTargetVersion);
    }
}