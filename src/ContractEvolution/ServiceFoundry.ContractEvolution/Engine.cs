namespace ServiceFoundry.ContractEvolution;

internal static class EvolutionDiagnosticCodes
{
    public const string AmbiguousPath = "EVO005";
    public const string DuplicateMap = "EVO003";
    public const string DuplicateVersion = "EVO002";
    public const string MissingLatest = "EVO001";
    public const string MissingTargetBinding = "EVO004";
}

internal enum BindingKind
{
    Rename,
    Default,
    Compute,
}

internal sealed record BindingRegistration(
    BindingKind Kind,
    string TargetPropertyName,
    string? SourcePropertyName,
    object? DefaultValue,
    Func<object, object?>? Compute);

internal sealed class FamilyRegistration
{
    public FamilyRegistration(Type contractType, string familyName)
    {
        ContractType = contractType;
        FamilyName = familyName;
    }

    public Type ContractType { get; }

    public string FamilyName { get; }

    public List<MapRegistration> Maps { get; } = new();

    public Dictionary<string, VersionRegistration> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void EnsureVersion(ContractVersion version, Type clrType, bool isLatest)
    {
        if (!Versions.TryGetValue(version.Value, out var existing))
        {
            Versions.Add(version.Value, new VersionRegistration(new ContractIdentity(FamilyName, version), clrType, isLatest));
            return;
        }

        Versions[version.Value] = existing with
        {
            ClrType = clrType,
            IsLatest = existing.IsLatest || isLatest,
        };
    }
}

internal sealed record VersionRegistration(ContractIdentity Identity, Type ClrType, bool IsLatest);

internal sealed record MapRegistration(
    ContractVersion SourceVersion,
    ContractVersion TargetVersion,
    Type SourceType,
    Type TargetType,
    IReadOnlyList<BindingRegistration> Bindings);

internal sealed record CompiledVersion(ContractIdentity Identity, Type ClrType);

internal sealed record CompiledEdge(
    CompiledVersion Source,
    CompiledVersion Target,
    Func<object, object> Mapper,
    CompatibilityAssessment Assessment);

internal sealed class CompiledFamily
{
    public required Type ContractType { get; init; }

    public required string FamilyName { get; init; }

    public required CompiledVersion Latest { get; init; }

    public required IReadOnlyDictionary<string, List<CompiledEdge>> OutgoingEdges { get; init; }

    public required IReadOnlyDictionary<string, CompiledVersion> Versions { get; init; }
}

internal sealed class ContractEvolutionEngine : IContractEvolutionEngine, IContractEvolutionReportProvider
{
    private readonly IReadOnlyDictionary<Type, CompiledFamily> _families;

    private ContractEvolutionEngine(IReadOnlyDictionary<Type, CompiledFamily> families)
    {
        _families = families;
    }

    public static IContractEvolutionEngine Build(IEnumerable<FamilyRegistration> registrations)
    {
        var diagnostics = new List<EvolutionDiagnostic>();
        var families = new Dictionary<Type, CompiledFamily>();

        foreach (var registration in registrations)
        {
            diagnostics.AddRange(ValidateRegistration(registration));
            if (diagnostics.Count > 0)
            {
                continue;
            }

            var versions = registration.Versions.Values.ToDictionary(
                version => version.Identity.Version.Value,
                version => new CompiledVersion(version.Identity, version.ClrType),
                StringComparer.OrdinalIgnoreCase);

            var latest = versions.Values.Single(version => registration.Versions[version.Identity.Version.Value].IsLatest);
            var outgoingEdges = new Dictionary<string, List<CompiledEdge>>(StringComparer.OrdinalIgnoreCase);

            foreach (var map in registration.Maps)
            {
                if (!versions.TryGetValue(map.SourceVersion.Value, out var source) || !versions.TryGetValue(map.TargetVersion.Value, out var target))
                {
                    continue;
                }

                var edgeDiagnostics = new List<EvolutionDiagnostic>();
                var compiledEdge = CompileEdge(registration, source, target, map, edgeDiagnostics);
                diagnostics.AddRange(edgeDiagnostics);
                if (compiledEdge is null)
                {
                    continue;
                }

                if (!outgoingEdges.TryGetValue(source.Identity.Version.Value, out var edges))
                {
                    edges = new List<CompiledEdge>();
                    outgoingEdges.Add(source.Identity.Version.Value, edges);
                }

                edges.Add(compiledEdge);
            }

            var compiledFamily = new CompiledFamily
            {
                ContractType = registration.ContractType,
                FamilyName = registration.FamilyName,
                Latest = latest,
                OutgoingEdges = outgoingEdges,
                Versions = versions,
            };

            diagnostics.AddRange(ValidateShortestPaths(compiledFamily));
            families[registration.ContractType] = compiledFamily;
        }

        if (diagnostics.Count > 0)
        {
            throw new ContractEvolutionValidationException(diagnostics);
        }

        return new ContractEvolutionEngine(families);
    }

    public CompatibilityAssessment Assess(Type contractType, ContractVersion sourceVersion, ContractVersion targetVersion)
        => ResolvePath(GetFamily(contractType), sourceVersion, targetVersion).Plan.Assessment;

    public Type GetClrType(Type contractType, ContractVersion version)
        => GetFamily(contractType).Versions.TryGetValue(version.Value, out var compiledVersion)
            ? compiledVersion.ClrType
            : throw new InvalidOperationException($"Contract '{contractType.Name}' has no registered version '{version.Value}'.");

    public ContractIdentity GetLatestIdentity(Type contractType)
        => GetFamily(contractType).Latest.Identity;

    public ContractUpgradePlan ResolvePlan(Type contractType, ContractVersion sourceVersion, ContractVersion targetVersion)
        => ResolvePath(GetFamily(contractType), sourceVersion, targetVersion).Plan;

    public ContractFamilyReport GetReport(Type contractType)
        => BuildReport(GetFamily(contractType));

    public IReadOnlyList<ContractFamilyReport> GetReports()
        => _families.Values
            .OrderBy(family => family.FamilyName, StringComparer.OrdinalIgnoreCase)
            .Select(BuildReport)
            .ToArray();

    public object Upgrade(Type contractType, object source, ContractVersion sourceVersion, ContractVersion targetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);

        var family = GetFamily(contractType);
        var resolved = ResolvePath(family, sourceVersion, targetVersion);
        if (resolved.Plan.Assessment == CompatibilityAssessment.Breaking)
        {
            throw new InvalidOperationException($"No upgrade path exists from '{sourceVersion.Value}' to '{targetVersion.Value}' for contract '{family.FamilyName}'.");
        }

        var current = source;
        foreach (var edge in resolved.Edges)
        {
            current = edge.Mapper(current);
        }

        return current;
    }

    private static CompiledEdge? CompileEdge(
        FamilyRegistration registration,
        CompiledVersion source,
        CompiledVersion target,
        MapRegistration map,
        ICollection<EvolutionDiagnostic> diagnostics)
    {
        var targetType = target.ClrType;
        if (targetType.GetConstructor(Type.EmptyTypes) is null)
        {
            diagnostics.Add(new EvolutionDiagnostic(
                EvolutionDiagnosticCodes.MissingTargetBinding,
                $"Target type '{targetType.Name}' must have a public parameterless constructor.",
                source.Identity,
                target.Identity));
            return null;
        }

        var explicitBindings = map.Bindings.ToDictionary(binding => binding.TargetPropertyName, StringComparer.OrdinalIgnoreCase);
        var sourceProperties = source.ClrType.GetProperties().ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        var targetProperties = targetType.GetProperties()
            .Where(property => property.CanWrite && property.SetMethod?.IsPublic == true)
            .ToArray();

        var actions = new List<Action<object, object>>();
        var hasCustomBinding = false;

        foreach (var targetProperty in targetProperties)
        {
            if (explicitBindings.TryGetValue(targetProperty.Name, out var binding))
            {
                hasCustomBinding = true;
                switch (binding.Kind)
                {
                    case BindingKind.Rename:
                    {
                        if (binding.SourcePropertyName is null || !sourceProperties.TryGetValue(binding.SourcePropertyName, out var sourceProperty))
                        {
                            diagnostics.Add(new EvolutionDiagnostic(
                                EvolutionDiagnosticCodes.MissingTargetBinding,
                                $"Binding for target member '{targetProperty.Name}' references missing source member '{binding.SourcePropertyName}'.",
                                source.Identity,
                                target.Identity));
                            continue;
                        }

                        if (!targetProperty.PropertyType.IsAssignableFrom(sourceProperty.PropertyType))
                        {
                            diagnostics.Add(new EvolutionDiagnostic(
                                EvolutionDiagnosticCodes.MissingTargetBinding,
                                $"Target member '{targetProperty.Name}' cannot be assigned from source member '{sourceProperty.Name}'.",
                                source.Identity,
                                target.Identity));
                            continue;
                        }

                        actions.Add((sourceObject, targetObject) => targetProperty.SetValue(targetObject, sourceProperty.GetValue(sourceObject)));
                        break;
                    }
                    case BindingKind.Default:
                        actions.Add((_, targetObject) => targetProperty.SetValue(targetObject, binding.DefaultValue));
                        break;
                    case BindingKind.Compute:
                        if (binding.Compute is null)
                        {
                            diagnostics.Add(new EvolutionDiagnostic(
                                EvolutionDiagnosticCodes.MissingTargetBinding,
                                $"Binding for target member '{targetProperty.Name}' does not provide a compute delegate.",
                                source.Identity,
                                target.Identity));
                            continue;
                        }

                        actions.Add((sourceObject, targetObject) => targetProperty.SetValue(targetObject, binding.Compute(sourceObject)));
                        break;
                }

                continue;
            }

            if (sourceProperties.TryGetValue(targetProperty.Name, out var defaultSourceProperty)
                && targetProperty.PropertyType.IsAssignableFrom(defaultSourceProperty.PropertyType))
            {
                actions.Add((sourceObject, targetObject) => targetProperty.SetValue(targetObject, defaultSourceProperty.GetValue(sourceObject)));
                continue;
            }

            diagnostics.Add(new EvolutionDiagnostic(
                EvolutionDiagnosticCodes.MissingTargetBinding,
                $"Target member '{targetProperty.Name}' is not satisfied by copy, rename, default, or compute.",
                source.Identity,
                target.Identity));
        }

        if (diagnostics.Count > 0)
        {
            return null;
        }

        object Mapper(object sourceObject)
        {
            var targetObject = Activator.CreateInstance(targetType)!;
            foreach (var action in actions)
            {
                action(sourceObject, targetObject);
            }

            return targetObject;
        }

        return new CompiledEdge(source, target, Mapper, hasCustomBinding ? CompatibilityAssessment.Upgradeable : CompatibilityAssessment.Compatible);
    }

    private CompiledFamily GetFamily(Type contractType)
        => _families.TryGetValue(contractType, out var family)
            ? family
            : throw new InvalidOperationException($"Contract family '{contractType.Name}' is not registered.");

    private static ContractFamilyReport BuildReport(CompiledFamily family)
    {
        var versions = family.Versions.Values
            .OrderBy(version => version.Identity.Version.Value, StringComparer.OrdinalIgnoreCase)
            .Select(version => new ContractVersionReport(
                version.Identity,
                version.ClrType.FullName ?? version.ClrType.Name,
                string.Equals(version.Identity.Version.Value, family.Latest.Identity.Version.Value, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var edges = family.OutgoingEdges.Values
            .SelectMany(static group => group)
            .OrderBy(edge => edge.Source.Identity.Version.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.Target.Identity.Version.Value, StringComparer.OrdinalIgnoreCase)
            .Select(edge => new ContractEdgeReport(edge.Source.Identity, edge.Target.Identity, edge.Assessment))
            .ToArray();

        return new ContractFamilyReport(family.FamilyName, family.Latest.Identity, versions, edges);
    }

    private static ResolvedPath ResolvePath(CompiledFamily family, ContractVersion sourceVersion, ContractVersion targetVersion)
    {
        if (!family.Versions.TryGetValue(sourceVersion.Value, out var source))
        {
            throw new InvalidOperationException($"Contract '{family.FamilyName}' has no registered version '{sourceVersion.Value}'.");
        }

        if (!family.Versions.TryGetValue(targetVersion.Value, out var target))
        {
            throw new InvalidOperationException($"Contract '{family.FamilyName}' has no registered version '{targetVersion.Value}'.");
        }

        if (string.Equals(sourceVersion.Value, targetVersion.Value, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedPath(new ContractUpgradePlan(source.Identity, target.Identity, new[] { source.Identity }, CompatibilityAssessment.Compatible), Array.Empty<CompiledEdge>());
        }

        var search = FindShortestPaths(family, sourceVersion, targetVersion);
        if (search.Paths.Count == 0)
        {
            return new ResolvedPath(new ContractUpgradePlan(source.Identity, target.Identity, new[] { source.Identity, target.Identity }, CompatibilityAssessment.Breaking), Array.Empty<CompiledEdge>());
        }

        if (search.Paths.Count > 1)
        {
            throw new InvalidOperationException($"Multiple equally short upgrade paths exist from '{sourceVersion.Value}' to '{targetVersion.Value}' for contract '{family.FamilyName}'.");
        }

        var path = search.Paths[0];
        var pathIdentities = new List<ContractIdentity> { source.Identity };
        pathIdentities.AddRange(path.Select(edge => edge.Target.Identity));

        var assessment = path.Any(edge => edge.Assessment == CompatibilityAssessment.Upgradeable)
            ? CompatibilityAssessment.Upgradeable
            : CompatibilityAssessment.Compatible;

        return new ResolvedPath(new ContractUpgradePlan(source.Identity, target.Identity, pathIdentities, assessment), path);
    }

    private static IEnumerable<EvolutionDiagnostic> ValidateRegistration(FamilyRegistration registration)
    {
        var diagnostics = new List<EvolutionDiagnostic>();
        if (registration.Versions.Count == 0)
        {
            diagnostics.Add(new EvolutionDiagnostic(EvolutionDiagnosticCodes.MissingLatest, $"Contract family '{registration.FamilyName}' has no registered versions."));
            return diagnostics;
        }

        if (registration.Versions.Values.Count(version => version.IsLatest) != 1)
        {
            diagnostics.Add(new EvolutionDiagnostic(EvolutionDiagnosticCodes.MissingLatest, $"Contract family '{registration.FamilyName}' must declare exactly one latest version."));
        }

        var duplicateMaps = registration.Maps
            .GroupBy(map => $"{map.SourceVersion.Value}->{map.TargetVersion.Value}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);

        foreach (var duplicate in duplicateMaps)
        {
            var first = duplicate.First();
            diagnostics.Add(new EvolutionDiagnostic(
                EvolutionDiagnosticCodes.DuplicateMap,
                $"Contract family '{registration.FamilyName}' declares multiple direct maps from '{first.SourceVersion.Value}' to '{first.TargetVersion.Value}'."));
        }

        return diagnostics;
    }

    private static IEnumerable<EvolutionDiagnostic> ValidateShortestPaths(CompiledFamily family)
    {
        var diagnostics = new List<EvolutionDiagnostic>();
        var versions = family.Versions.Keys.ToArray();

        foreach (var sourceVersion in versions)
        {
            foreach (var targetVersion in versions)
            {
                if (string.Equals(sourceVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var search = FindShortestPaths(family, new ContractVersion(sourceVersion), new ContractVersion(targetVersion));
                if (search.Paths.Count > 1)
                {
                    diagnostics.Add(new EvolutionDiagnostic(
                        EvolutionDiagnosticCodes.AmbiguousPath,
                        $"Contract family '{family.FamilyName}' has multiple equally short upgrade paths from '{sourceVersion}' to '{targetVersion}'.",
                        family.Versions[sourceVersion].Identity,
                        family.Versions[targetVersion].Identity));
                }
            }
        }

        return diagnostics;
    }

    private static PathSearchResult FindShortestPaths(CompiledFamily family, ContractVersion sourceVersion, ContractVersion targetVersion)
    {
        var queue = new Queue<PathCandidate>();
        queue.Enqueue(new PathCandidate(sourceVersion.Value, Array.Empty<CompiledEdge>()));

        var bestDistance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceVersion.Value] = 0,
        };

        var shortestLength = int.MaxValue;
        var bestPaths = new List<IReadOnlyList<CompiledEdge>>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Edges.Count > shortestLength)
            {
                continue;
            }

            if (string.Equals(current.Version, targetVersion.Value, StringComparison.OrdinalIgnoreCase))
            {
                shortestLength = current.Edges.Count;
                bestPaths.Add(current.Edges);
                continue;
            }

            if (!family.OutgoingEdges.TryGetValue(current.Version, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                var nextEdges = current.Edges.Concat(new[] { edge }).ToArray();
                if (nextEdges.Length > shortestLength)
                {
                    continue;
                }

                if (bestDistance.TryGetValue(edge.Target.Identity.Version.Value, out var existingDistance) && nextEdges.Length > existingDistance)
                {
                    continue;
                }

                bestDistance[edge.Target.Identity.Version.Value] = nextEdges.Length;
                queue.Enqueue(new PathCandidate(edge.Target.Identity.Version.Value, nextEdges));
            }
        }

        var uniquePaths = bestPaths
            .Select(path => path.ToArray())
            .Distinct(PathSignatureComparer.Instance)
            .Cast<IReadOnlyList<CompiledEdge>>()
            .ToList();

        return new PathSearchResult(uniquePaths);
    }

    private sealed record PathCandidate(string Version, IReadOnlyList<CompiledEdge> Edges);

    private sealed record PathSearchResult(IReadOnlyList<IReadOnlyList<CompiledEdge>> Paths);

    private sealed record ResolvedPath(ContractUpgradePlan Plan, IReadOnlyList<CompiledEdge> Edges);

    private sealed class PathSignatureComparer : IEqualityComparer<CompiledEdge[]>
    {
        public static PathSignatureComparer Instance { get; } = new();

        public bool Equals(CompiledEdge[]? x, CompiledEdge[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Length != y.Length)
            {
                return false;
            }

            for (var index = 0; index < x.Length; index++)
            {
                if (!string.Equals(x[index].Source.Identity.Version.Value, y[index].Source.Identity.Version.Value, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(x[index].Target.Identity.Version.Value, y[index].Target.Identity.Version.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(CompiledEdge[] obj)
        {
            var hash = new HashCode();
            foreach (var edge in obj)
            {
                hash.Add(edge.Source.Identity.Version.Value, StringComparer.OrdinalIgnoreCase);
                hash.Add(edge.Target.Identity.Version.Value, StringComparer.OrdinalIgnoreCase);
            }

            return hash.ToHashCode();
        }
    }
}