using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ServiceFoundry.ContractEvolution.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingTargetBindingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SFCE001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Target member is not bound by the contract map",
        "Target member '{0}' is not satisfied by copy, rename, default, or compute in ContractEvolution.Map<{1}, {2}>",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Flags target properties that the current ContractEvolution map will reject at runtime because they are not covered by copy-by-convention or an explicit mapping rule.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var targetMethod = invocation.TargetMethod;

        if (!string.Equals(targetMethod.Name, "Map", StringComparison.Ordinal)
            || targetMethod.TypeArguments.Length != 2
            || !IsContractFamilyBuilder(targetMethod.ContainingType))
        {
            return;
        }

        var sourceType = targetMethod.TypeArguments[0];
        var destinationType = targetMethod.TypeArguments[1];
        if (sourceType.TypeKind == TypeKind.Error || destinationType.TypeKind == TypeKind.Error)
        {
            return;
        }

        var configureArgument = invocation.Arguments.Length >= 3 ? invocation.Arguments[2].Value : null;
        var explicitTargets = CollectExplicitTargets(configureArgument);
        var sourceProperties = sourceType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(static property => !property.IsStatic && property.GetMethod is not null)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var targetProperty in destinationType.GetMembers().OfType<IPropertySymbol>())
        {
            if (targetProperty.IsStatic || targetProperty.SetMethod is null || targetProperty.SetMethod.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (explicitTargets.Contains(targetProperty.Name))
            {
                continue;
            }

            if (sourceProperties.TryGetValue(targetProperty.Name, out var sourceProperty)
                && context.Compilation.ClassifyCommonConversion(sourceProperty.Type, targetProperty.Type).IsImplicit)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                invocation.Syntax.GetLocation(),
                targetProperty.Name,
                sourceType.Name,
                destinationType.Name));
        }
    }

    private static HashSet<string> CollectExplicitTargets(IOperation? operation)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (operation is null)
        {
            return targets;
        }

        foreach (var invocation in Descend(operation).OfType<IInvocationOperation>())
        {
            if (!IsContractMapBuilder(invocation.Instance?.Type))
            {
                continue;
            }

            var targetArgument = invocation.TargetMethod.Name switch
            {
                "Rename" when invocation.Arguments.Length >= 2 => invocation.Arguments[1].Value,
                "Default" when invocation.Arguments.Length >= 1 => invocation.Arguments[0].Value,
                "Compute" when invocation.Arguments.Length >= 1 => invocation.Arguments[0].Value,
                _ => null,
            };

            var propertyName = TryGetPropertyName(targetArgument);
            if (propertyName is { Length: > 0 })
            {
                targets.Add(propertyName);
            }
        }

        return targets;
    }

    private static IEnumerable<IOperation> Descend(IOperation operation)
    {
        yield return operation;
        foreach (var child in operation.ChildOperations)
        {
            foreach (var descendant in Descend(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsContractFamilyBuilder(INamedTypeSymbol? type)
        => type is not null
           && string.Equals(type.Name, "ContractFamilyBuilder", StringComparison.Ordinal)
           && string.Equals(type.ContainingNamespace.ToDisplayString(), "ServiceFoundry.ContractEvolution", StringComparison.Ordinal);

    private static bool IsContractMapBuilder(ITypeSymbol? type)
        => type is INamedTypeSymbol namedType
           && string.Equals(namedType.Name, "ContractMapBuilder", StringComparison.Ordinal)
           && string.Equals(namedType.ContainingNamespace.ToDisplayString(), "ServiceFoundry.ContractEvolution", StringComparison.Ordinal);

    private static string? TryGetPropertyName(IOperation? operation)
        => operation switch
        {
            null => null,
            IArgumentOperation argument => TryGetPropertyName(argument.Value),
            IConversionOperation conversion => TryGetPropertyName(conversion.Operand),
            IDelegateCreationOperation delegateCreation when delegateCreation.Target is not null => TryGetPropertyName(delegateCreation.Target),
            IAnonymousFunctionOperation anonymousFunction => TryGetPropertyName(anonymousFunction.Body),
            IBlockOperation block when block.Operations.Length == 1 => TryGetPropertyName(block.Operations[0]),
            IReturnOperation returnOperation => TryGetPropertyName(returnOperation.ReturnedValue),
            IExpressionStatementOperation expressionStatement => TryGetPropertyName(expressionStatement.Operation),
            IPropertyReferenceOperation propertyReference => propertyReference.Property.Name,
            _ => null,
        };
}