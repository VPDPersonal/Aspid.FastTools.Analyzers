using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Aspid.FastTools.Analyzers.Descriptions;
using UnityAttributes = Aspid.FastTools.Analyzers.Descriptions.UnityEngine.AttributesDescription;
using UnityClasses = Aspid.FastTools.Analyzers.Descriptions.UnityEngine.ClassesDescription;
using AspidAttributes = Aspid.FastTools.Analyzers.Descriptions.AspidFastTools.AttributesDescription;
using AspidEnums = Aspid.FastTools.Analyzers.Descriptions.AspidFastTools.EnumsDescription;

namespace Aspid.FastTools.Analyzers;

/// <summary>
/// Validates <c>[TypeSelector]</c> usage. The attribute drives two field shapes — a <c>string</c> holding an
/// assembly-qualified type name, and a <c>[SerializeReference]</c> managed reference that is instantiated — so some
/// of its knobs apply only in one context. These diagnostics surface a misuse at compile time instead of at runtime.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AspidFastToolsAnalyzer : DiagnosticAnalyzer
{
    private const string ListDefinition = "System.Collections.Generic.List<T>";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            DiagnosticRules.TypeSelectorFieldTypeRule,
            DiagnosticRules.TypeSelectorAllowRule,
            DiagnosticRules.TypeSelectorBaseTypeRule,
            DiagnosticRules.TypeSelectorObjectDerivedRule,
            DiagnosticRules.TypeSelectorNoConcreteImplementationRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationContext =>
        {
            // Build the candidate-type list once per compilation, lazily, so the cost is paid only when a
            // [TypeSelector]+[SerializeReference] field is actually encountered (AFT0005 check).
            var lazyCandidates = new Lazy<ImmutableArray<INamedTypeSymbol>>(
                () => CollectConcreteCandidates(compilationContext.Compilation),
                LazyThreadSafetyMode.ExecutionAndPublication);

            compilationContext.RegisterSyntaxNodeAction(
                ctx => AnalyzeField(ctx, lazyCandidates),
                SyntaxKind.FieldDeclaration);
        });
    }

    private static void AnalyzeField(SyntaxNodeAnalysisContext context, Lazy<ImmutableArray<INamedTypeSymbol>> lazyCandidates)
    {
        var field = (FieldDeclarationSyntax)context.Node;

        var typeSelector = FindAttribute(field, context.SemanticModel, AspidAttributes.TypeSelectorFull);
        if (typeSelector is null) return;

        if (field.Declaration.Variables.Count == 0) return;
        if (context.SemanticModel.GetDeclaredSymbol(field.Declaration.Variables[0]) is not IFieldSymbol fieldSymbol) return;

        // Unwrap arrays / List<T> so the checks see the element type a [SerializeReference] entry actually holds.
        var elementType = GetElementType(fieldSymbol.Type);
        var isString = elementType.SpecialType == SpecialType.System_String;
        var isManagedReference = FindAttribute(field, context.SemanticModel, UnityAttributes.SerializeReferenceFull) is not null;

        // AFT0001 — neither a string type-name field nor a [SerializeReference] managed reference: the drawer throws.
        if (!isString && !isManagedReference)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticRules.TypeSelectorFieldTypeRule, typeSelector.GetLocation(), fieldSymbol.Name));
            return;
        }

        // On a string field both Allow and the base types are meaningful (a Type is named, not instantiated).
        if (!isManagedReference) return;

        ReportAllowOnManagedReference(context, typeSelector, fieldSymbol.Name);

        // Collect bases that are already provably disjoint from the field type (AFT0003). AFT0005 skips those
        // bases because AFT0003 already says the selector is empty — reporting both would be redundant noise.
        var disjointBases = ReportDisjointBaseTypes(context, typeSelector, elementType);

        // AFT0004 — element type derives from UnityEngine.Object: Unity silently skips it for managed references.
        if (ReportObjectDerivedManagedReference(context, typeSelector, fieldSymbol.Name, elementType)) return;

        // AFT0005 — no visible concrete implementation exists for the effective base set.
        ReportNoConcreteImplementation(context, typeSelector, fieldSymbol.Name, elementType, lazyCandidates, disjointBases);
    }

    // AFT0002 — Allow opts abstract classes / interfaces into the candidate list, which cannot be instantiated for a
    // managed reference, so the flag is silently ignored on this path.
    private static void ReportAllowOnManagedReference(SyntaxNodeAnalysisContext context, AttributeSyntax typeSelector, string fieldName)
    {
        if (typeSelector.ArgumentList is null) return;

        foreach (var argument in typeSelector.ArgumentList.Arguments)
        {
            if (argument.NameEquals?.Name.Identifier.ValueText != AspidEnums.AllowArgument) continue;

            var constant = context.SemanticModel.GetConstantValue(argument.Expression);
            if (!constant.HasValue || constant.Value is null) return;

            try
            {
                if (Convert.ToInt64(constant.Value) != AspidEnums.TypeAllowNone)
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticRules.TypeSelectorAllowRule, argument.GetLocation(), fieldName));
            }
            catch (Exception)
            {
                // A non-integral constant cannot be a TypeAllow flag — nothing to report.
            }

            return;
        }
    }

    // AFT0003 — a typeof(...) base type unrelated to the field's element type narrows the candidate list to nothing.
    // Returns the set of base types that were reported as disjoint so AFT0005 can skip them (no double-reporting).
    private static ImmutableHashSet<ITypeSymbol> ReportDisjointBaseTypes(
        SyntaxNodeAnalysisContext context, AttributeSyntax typeSelector, ITypeSymbol fieldElementType)
    {
        if (typeSelector.ArgumentList is null) return ImmutableHashSet<ITypeSymbol>.Empty;

        ImmutableHashSet<ITypeSymbol>.Builder? disjoint = null;

        foreach (var argument in typeSelector.ArgumentList.Arguments)
        {
            if (argument.NameEquals is not null) continue;                          // skip Allow = ...
            if (argument.Expression is not TypeOfExpressionSyntax typeOf) continue;  // only typeof(...) args are statically checkable

            if (context.SemanticModel.GetTypeInfo(typeOf.Type).Type is not { } baseType) continue;
            if (baseType.SpecialType == SpecialType.System_Object) continue;         // the unconstrained default narrows nothing

            if (AreProvablyDisjoint(baseType, fieldElementType))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticRules.TypeSelectorBaseTypeRule, typeOf.GetLocation(), baseType.Name, fieldElementType.Name));
                disjoint ??= ImmutableHashSet.CreateBuilder<ITypeSymbol>(SymbolEqualityComparer.Default);
                disjoint.Add(baseType);
            }
        }

        return disjoint?.ToImmutable() ?? ImmutableHashSet<ITypeSymbol>.Empty;
    }

    // AFT0004 — element type derives from UnityEngine.Object: Unity silently does not serialize such types as
    // managed references. The user should drop [SerializeReference] and use a plain object reference.
    // Returns true when the diagnostic was reported (so the caller can skip further managed-reference checks).
    private static bool ReportObjectDerivedManagedReference(
        SyntaxNodeAnalysisContext context, AttributeSyntax typeSelector, string fieldName, ITypeSymbol elementType)
    {
        if (!IsUnityObjectDerived(elementType)) return false;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticRules.TypeSelectorObjectDerivedRule,
            typeSelector.GetLocation(),
            fieldName,
            elementType.Name));

        return true;
    }

    // AFT0005 — no visible concrete (non-abstract, non-UnityEngine.Object, non-delegate, non-string) class exists
    // that implements the effective base type. Severity is Warning only because implementations may live in
    // downstream assemblies the compilation cannot see.
    // disjointBases: bases already reported by AFT0003 — skip them here to avoid redundant diagnostics.
    private static void ReportNoConcreteImplementation(
        SyntaxNodeAnalysisContext context,
        AttributeSyntax typeSelector,
        string fieldName,
        ITypeSymbol elementType,
        Lazy<ImmutableArray<INamedTypeSymbol>> lazyCandidates,
        ImmutableHashSet<ITypeSymbol> disjointBases)
    {
        // Collect the effective base set: typeof(...) arguments when present, otherwise the element type itself.
        var bases = CollectEffectiveBases(typeSelector, context.SemanticModel, elementType);

        foreach (var baseType in bases)
        {
            // Skip bases already covered by AFT0003 (provably disjoint from the field type).
            if (disjointBases.Contains(baseType)) continue;

            // Skip the check when the base itself is a concrete instantiable class — it is its own candidate.
            if (IsConcreteInstantiable(baseType) && !IsUnityObjectDerived(baseType)) continue;

            var candidates = lazyCandidates.Value;
            if (HasVisibleCandidate(baseType, candidates)) continue;

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticRules.TypeSelectorNoConcreteImplementationRule,
                typeSelector.GetLocation(),
                fieldName,
                baseType.Name));
        }
    }

    // Collect the typeof(...) positional arguments from [TypeSelector(...)] as the effective base set. If none are
    // present the element type itself is the sole base (the picker defaults to it).
    private static ImmutableArray<ITypeSymbol> CollectEffectiveBases(
        AttributeSyntax typeSelector, SemanticModel model, ITypeSymbol elementType)
    {
        if (typeSelector.ArgumentList is null) return ImmutableArray.Create(elementType);

        var builder = ImmutableArray.CreateBuilder<ITypeSymbol>();
        foreach (var argument in typeSelector.ArgumentList.Arguments)
        {
            if (argument.NameEquals is not null) continue;                           // skip Allow = ...
            if (argument.Expression is not TypeOfExpressionSyntax typeOf) continue;  // only typeof(...) args

            if (model.GetTypeInfo(typeOf.Type).Type is { } t) builder.Add(t);
        }

        return builder.Count > 0 ? builder.ToImmutable() : ImmutableArray.Create(elementType);
    }

    // Returns true when at least one candidate in the compilation is assignable to baseType.
    // For an open generic definition, assignability is tested against the original definition to avoid needing
    // concrete type arguments (any closed form would still be assignable to the base).
    private static bool HasVisibleCandidate(ITypeSymbol baseType, ImmutableArray<INamedTypeSymbol> candidates)
    {
        var baseOriginal = (baseType as INamedTypeSymbol)?.OriginalDefinition ?? baseType;

        foreach (var candidate in candidates)
        {
            // For open generic candidates test their original definition against the base's original definition;
            // this avoids false negatives when the candidate matches a generic base.
            var testFrom = candidate.IsGenericType ? candidate.OriginalDefinition : candidate;
            var testTo   = baseType.IsDefinition ? baseType : baseOriginal;

            if (IsAssignableTo(testFrom, testTo)) return true;
        }

        return false;
    }

    // Collects every named type from the compilation's global namespace (source + references) that is a concrete,
    // non-abstract, non-static class, not derived from UnityEngine.Object, not string, not a delegate.
    private static ImmutableArray<INamedTypeSymbol> CollectConcreteCandidates(Compilation compilation)
    {
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        CollectFromNamespace(compilation.GlobalNamespace, builder);
        return builder.ToImmutable();
    }

    private static void CollectFromNamespace(INamespaceSymbol ns, ImmutableArray<INamedTypeSymbol>.Builder builder)
    {
        foreach (var type in ns.GetTypeMembers())
            CollectFromType(type, builder);

        foreach (var nested in ns.GetNamespaceMembers())
            CollectFromNamespace(nested, builder);
    }

    private static void CollectFromType(INamedTypeSymbol type, ImmutableArray<INamedTypeSymbol>.Builder builder)
    {
        if (IsConcreteInstantiable(type) && !IsUnityObjectDerived(type))
            builder.Add(type);

        // Recurse into nested types.
        foreach (var nested in type.GetTypeMembers())
            CollectFromType(nested, builder);
    }

    private static bool IsConcreteInstantiable(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (named.TypeKind != TypeKind.Class) return false;
        if (named.IsAbstract || named.IsStatic) return false;
        if (named.SpecialType == SpecialType.System_String) return false;
        if (named.TypeKind == TypeKind.Delegate) return false;

        return true;
    }

    // Walks the base chain to find UnityEngine.Object by full display name. Intentionally matches both the real
    // Unity type and the test stub (both share the same namespace + class name).
    private static bool IsUnityObjectDerived(ITypeSymbol type)
    {
        for (var t = type as INamedTypeSymbol; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == UnityClasses.ObjectFull) return true;

        return false;
    }

    private static AttributeSyntax? FindAttribute(FieldDeclarationSyntax field, SemanticModel model, string fullName)
    {
        foreach (var attribute in field.AttributeLists.SelectMany(list => list.Attributes))
        {
            if (model.GetSymbolInfo(attribute).Symbol is not IMethodSymbol constructor) continue;
            if (constructor.ContainingType.ToDisplayString() == fullName) return attribute;
        }

        return null;
    }

    private static ITypeSymbol GetElementType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;

        if (type is INamedTypeSymbol { IsGenericType: true } named &&
            named.OriginalDefinition.ToDisplayString() == ListDefinition)
            return named.TypeArguments[0];

        return type;
    }

    // Two non-interface types with no inheritance relationship can share no concrete instance (single inheritance),
    // so the selector would be empty. An interface paired with a class is only provably disjoint when the class is
    // sealed and does not implement it — no further subtype can add the interface. Two interfaces are never provably
    // disjoint (one class can implement both), so they are left alone to avoid false positives.
    private static bool AreProvablyDisjoint(ITypeSymbol baseType, ITypeSymbol fieldType)
    {
        var baseIsInterface = baseType.TypeKind == TypeKind.Interface;
        var fieldIsInterface = fieldType.TypeKind == TypeKind.Interface;

        if (baseIsInterface && fieldIsInterface) return false;

        if (baseIsInterface || fieldIsInterface)
        {
            var contract = baseIsInterface ? baseType : fieldType;
            var implementation = baseIsInterface ? fieldType : baseType;

            return implementation.IsSealed && !IsAssignableTo(implementation, contract);
        }

        return !IsAssignableTo(baseType, fieldType) && !IsAssignableTo(fieldType, baseType);
    }

    private static bool IsAssignableTo(ITypeSymbol from, ITypeSymbol to)
    {
        if (SymbolEqualityComparer.Default.Equals(from, to)) return true;

        for (var baseType = from.BaseType; baseType is not null; baseType = baseType.BaseType)
            if (SymbolEqualityComparer.Default.Equals(baseType, to)) return true;

        if (to.TypeKind == TypeKind.Interface)
            foreach (var contract in from.AllInterfaces)
                if (SymbolEqualityComparer.Default.Equals(contract, to)) return true;

        return false;
    }
}
