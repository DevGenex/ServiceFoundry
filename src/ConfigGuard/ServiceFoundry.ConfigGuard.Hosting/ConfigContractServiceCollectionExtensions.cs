using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServiceFoundry.ConfigGuard.Hosting;

public static class ConfigContractServiceCollectionExtensions
{
    public static ConfigContractRegistrationBuilder<TOptions> AddConfigContract<TOptions>(
        this IServiceCollection services,
        string sectionPath,
        Action<ConfigContractBuilder<TOptions>> configure,
        BindingPolicy? bindingPolicy = null)
        where TOptions : class, new()
        => services.AddNamedConfigContract(Options.DefaultName, sectionPath, configure, bindingPolicy);

    public static ConfigContractRegistrationBuilder<TOptions> AddNamedConfigContract<TOptions>(
        this IServiceCollection services,
        string optionsName,
        string sectionPath,
        Action<ConfigContractBuilder<TOptions>> configure,
        BindingPolicy? bindingPolicy = null)
        where TOptions : class, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        var contract = ConfigContract.Create(sectionPath, configure, bindingPolicy);
        var registration = new ConfigContractRegistration<TOptions>(NormalizeOptionsName(optionsName), contract);

        services.AddSingleton(contract);
        services.AddSingleton(registration);
        services.AddSingleton<IValidateOptions<TOptions>, ConfigContractOptionsValidator<TOptions>>();
        services.AddOptions<TOptions>(registration.OptionsName)
            .Configure<IConfiguration>((options, configuration) => registration.Contract.Apply(configuration, options));

        if (string.Equals(registration.OptionsName, Options.DefaultName, StringComparison.Ordinal))
        {
            services.TryAddSingleton(static serviceProvider => serviceProvider.GetRequiredService<IOptions<TOptions>>().Value);
        }

        return new ConfigContractRegistrationBuilder<TOptions>(services);
    }

    private static string NormalizeOptionsName(string optionsName)
        => string.IsNullOrWhiteSpace(optionsName) ? Options.DefaultName : optionsName.Trim();
}

public sealed class ConfigContractRegistrationBuilder<TOptions> where TOptions : class, new()
{
    private readonly IServiceCollection _services;

    internal ConfigContractRegistrationBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public ConfigContractRegistrationBuilder<TOptions> FailFastOnStartup()
    {
        _services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConfigContractValidationHostedService<TOptions>>());
        return this;
    }
}

internal sealed record ConfigContractRegistration<TOptions>(string OptionsName, ConfigContract<TOptions> Contract)
    where TOptions : class, new();

internal sealed class ConfigContractOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : class, new()
{
    private readonly IConfiguration _configuration;
    private readonly IEnumerable<ConfigContractRegistration<TOptions>> _registrations;

    public ConfigContractOptionsValidator(
        IEnumerable<ConfigContractRegistration<TOptions>> registrations,
        IConfiguration configuration)
    {
        _registrations = registrations;
        _configuration = configuration;
    }

    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? Options.DefaultName : name;
        var registration = _registrations.FirstOrDefault(candidate => string.Equals(candidate.OptionsName, normalizedName, StringComparison.Ordinal));

        if (registration is null)
        {
            return ValidateOptionsResult.Skip;
        }

        var result = registration.Contract.Validate(_configuration);
        if (!result.HasErrors)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(result.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == ConfigDiagnosticSeverity.Error)
            .Select(diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"));
    }
}

internal sealed class ConfigContractValidationHostedService<TOptions> : IHostedService where TOptions : class, new()
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigContractValidationHostedService<TOptions>> _logger;
    private readonly IEnumerable<ConfigContractRegistration<TOptions>> _registrations;

    public ConfigContractValidationHostedService(
        IEnumerable<ConfigContractRegistration<TOptions>> registrations,
        IConfiguration configuration,
        ILogger<ConfigContractValidationHostedService<TOptions>> logger)
    {
        _registrations = registrations;
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in _registrations)
        {
            var result = registration.Contract.Validate(_configuration);
            foreach (var warning in result.Diagnostics.Where(static diagnostic => diagnostic.Severity == ConfigDiagnosticSeverity.Warning))
            {
                _logger.LogWarning("{Code}: {Message} (OptionsName: {OptionsName})", warning.Code, warning.Message, registration.OptionsName);
            }

            if (result.HasErrors)
            {
                throw new ConfigValidationException(result.Diagnostics.First().SectionPath, result.Diagnostics);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}