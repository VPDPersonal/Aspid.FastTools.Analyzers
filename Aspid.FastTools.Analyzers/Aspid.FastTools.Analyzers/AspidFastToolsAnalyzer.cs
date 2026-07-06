using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Aspid.FastTools.Analyzers.Descriptions;
using UnityAttributes = Aspid.FastTools.Analyzers.Descriptions.UnityEngine.AttributesDescription;
using UnityClasses = Aspid.FastTools.Analyzers.Descriptions.UnityEngine.ClassesDescription;
using AspidAttributes = Aspid.FastTools.Analyzers.Descriptions.AspidFastTools.AttributesDescription;
using AspidClasses = Aspid.FastTools.Analyzers.Descriptions.AspidFastTools.ClassesDescription;
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
            // One cache per compilation: the AFT0005 candidate search walks assembly metadata, so its results are
            // memoised per (base type, field element type) pair and the walk itself is limited to assemblies that
            // can actually contain a candidate (see CandidateSearch).
            var candidateSearch = new CandidateSearch(compilationContext.Compilation);

            compilationContext.RegisterSyntaxNodeAction(
                ctx => AnalyzeField(ctx, candidateSearch),
                SyntaxKind.FieldDeclaration);
        });
    }

    private static void AnalyzeField(SyntaxNodeAnalysisContext context, CandidateSearch candidateSearch)
    {
        var field = (FieldDeclarationSyntax)context.Node;

        var typeSelector = FindAttribute(field, context.SemanticModel, AspidAttributes.TypeSelectorFull);
        if (typeSelector is null) return;

        if (field.Declaration.Variables.Count == 0) return;
        if (context.SemanticModel.GetDeclaredSymbol(field.Declaration.Variables[0]) is not IFieldSymbol fieldSymbol) return;

        // Unwrap arrays / List<T> so the checks see the element type a [SerializeReference] entry actually holds.
        var elementType = GetElementType(fieldSymbol.Type);
        var isString = elementType.SpecialType == SpecialType.System_String;
        var isSerializableType = IsSerializableType(elementType);
        var isManagedReference = FindAttribute(field, context.SemanticModel, UnityAttributes.SerializeReferenceFull) is not null;

        // AFT0001 — none of the three valid shapes (a string type-name field, a SerializableType / SerializableType<T>
        // field, or a [SerializeReference] managed reference): the drawer throws.
        if (!isString && !isSerializableType && !isManagedReference)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticRules.TypeSelectorFieldTypeRule, typeSelector.GetLocation(), fieldSymbol.Name));
            return;
        }

        // On a string or SerializableType field both Allow and the base types are meaningful (a Type is named, not
        // instantiated), and none of the managed-reference-only checks below apply.
        if (!isManagedReference) return;

        ReportAllowOnManagedReference(context, typeSelector, fieldSymbol.Name);

        // Collect bases that are already provably disjoint from the field type (AFT0003). AFT0005 skips those
        // bases because AFT0003 already says the selector is empty — reporting both would be redundant noise.
        var disjointBases = ReportDisjointBaseTypes(context, typeSelector, elementType);

        // AFT0004 — element type derives from UnityEngine.Object: Unity silently skips it for managed references.
        if (ReportObjectDerivedManagedReference(context, typeSelector, fieldSymbol.Name, elementType)) return;

        // AFT0005 — no visible concrete implementation exists for the effective base set.
        ReportNoConcreteImplementation(context, typeSelector, fieldSymbol.Name, elementType, candidateSearch, disjointBases);
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
        CandidateSearch candidateSearch,
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

            if (candidateSearch.HasVisibleCandidate(baseType, elementType, context.CancellationToken)) continue;

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

    /// <summary>
    /// The AFT0005 candidate search: does any visible concrete class satisfy both the base type and the field's
    /// element type? A naive walk of <see cref="Compilation.GlobalNamespace"/> materialises every symbol of every
    /// referenced assembly (all of the BCL and UnityEngine — minutes of csc time in a Unity compilation), so the
    /// search instead only walks assemblies that could contain a candidate: a type assignable to the base must live
    /// in an assembly that declares or references the base's assembly (metadata cannot derive from an unreferenced
    /// type), and likewise for the field's element type. The walk stops at the first match, and results are memoised
    /// per (base type, field element type) pair for the lifetime of the compilation.
    /// </summary>
    private sealed class CandidateSearch
    {
        private readonly Compilation _compilation;
        private readonly ConcurrentDictionary<(ITypeSymbol Base, ITypeSymbol Field), bool> _results;

        public CandidateSearch(Compilation compilation)
        {
            _compilation = compilation;
            _results = new ConcurrentDictionary<(ITypeSymbol, ITypeSymbol), bool>(PairComparer.Instance);
        }

        // Returns true when at least one candidate in the compilation is assignable to BOTH baseType and
        // fieldElementType. The picker intersects the typeof(...) base set with the field's declared element type,
        // so a candidate must satisfy both constraints to be reachable. When fieldElementType is System.Object the
        // field constraint is trivially true for any candidate and is skipped.
        public bool HasVisibleCandidate(ITypeSymbol baseType, ITypeSymbol fieldElementType, CancellationToken cancellationToken)
        {
            if (_results.TryGetValue((baseType, fieldElementType), out var cached)) return cached;

            var result = Scan(baseType, fieldElementType, cancellationToken);
            return _results.GetOrAdd((baseType, fieldElementType), result);
        }

        private bool Scan(ITypeSymbol baseType, ITypeSymbol fieldElementType, CancellationToken cancellationToken)
        {
            var fieldIsObject = fieldElementType.SpecialType == SpecialType.System_Object;

            var baseAssembly  = baseType.OriginalDefinition.ContainingAssembly;
            var fieldAssembly = fieldIsObject ? null : fieldElementType.OriginalDefinition.ContainingAssembly;

            // The source assembly first — candidates most often live next to the field — then only the references
            // that can see both constraint types. A null constraint assembly (error type) filters nothing.
            if (ScanAssembly(_compilation.Assembly, baseType, fieldElementType, fieldIsObject, cancellationToken))
                return true;

            foreach (var reference in _compilation.SourceModule.ReferencedAssemblySymbols)
            {
                if (!Sees(reference, baseAssembly) || !Sees(reference, fieldAssembly)) continue;
                if (ScanAssembly(reference, baseType, fieldElementType, fieldIsObject, cancellationToken))
                    return true;
            }

            return false;
        }

        private static bool ScanAssembly(
            IAssemblySymbol assembly, ITypeSymbol baseType, ITypeSymbol fieldElementType, bool fieldIsObject,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ScanNamespace(assembly.GlobalNamespace, baseType, fieldElementType, fieldIsObject);
        }

        private static bool ScanNamespace(
            INamespaceSymbol ns, ITypeSymbol baseType, ITypeSymbol fieldElementType, bool fieldIsObject)
        {
            foreach (var type in ns.GetTypeMembers())
                if (ScanType(type, baseType, fieldElementType, fieldIsObject)) return true;

            foreach (var nested in ns.GetNamespaceMembers())
                if (ScanNamespace(nested, baseType, fieldElementType, fieldIsObject)) return true;

            return false;
        }

        private static bool ScanType(
            INamedTypeSymbol type, ITypeSymbol baseType, ITypeSymbol fieldElementType, bool fieldIsObject)
        {
            if (IsCandidate(type, baseType, fieldElementType, fieldIsObject)) return true;

            // Recurse into nested types.
            foreach (var nested in type.GetTypeMembers())
                if (ScanType(nested, baseType, fieldElementType, fieldIsObject)) return true;

            return false;
        }

        // A candidate is a concrete, non-abstract, non-static class, not derived from UnityEngine.Object, not string,
        // not a delegate, assignable to both constraint types. For open generic candidates assignability is tested
        // against original definitions to avoid needing concrete type arguments (any closed form would still be
        // assignable to both bases). Cheapest checks first: the UnityEngine.Object walk only runs on a type that
        // already matched both constraints.
        private static bool IsCandidate(
            INamedTypeSymbol type, ITypeSymbol baseType, ITypeSymbol fieldElementType, bool fieldIsObject)
        {
            if (!IsConcreteInstantiable(type)) return false;

            var testFrom = type.IsGenericType ? type.OriginalDefinition : type;

            var testToBase = baseType.IsDefinition
                ? baseType
                : (baseType as INamedTypeSymbol)?.OriginalDefinition ?? baseType;

            if (!IsAssignableTo(testFrom, testToBase)) return false;

            if (!fieldIsObject)
            {
                var testToField = fieldElementType.IsDefinition
                    ? fieldElementType
                    : (fieldElementType as INamedTypeSymbol)?.OriginalDefinition ?? fieldElementType;

                if (!IsAssignableTo(testFrom, testToField)) return false;
            }

            return !IsUnityObjectDerived(type);
        }

        // True when the assembly is (or references) the target, i.e. its metadata can declare a type derived from a
        // type of the target. A null target (unresolved constraint type) filters nothing.
        private static bool Sees(IAssemblySymbol assembly, IAssemblySymbol? target)
        {
            if (target is null) return true;
            if (SymbolEqualityComparer.Default.Equals(assembly, target)) return true;

            foreach (var module in assembly.Modules)
                foreach (var referenced in module.ReferencedAssemblySymbols)
                    if (SymbolEqualityComparer.Default.Equals(referenced, target)) return true;

            return false;
        }

        private sealed class PairComparer : IEqualityComparer<(ITypeSymbol Base, ITypeSymbol Field)>
        {
            public static readonly PairComparer Instance = new();

            public bool Equals((ITypeSymbol Base, ITypeSymbol Field) x, (ITypeSymbol Base, ITypeSymbol Field) y) =>
                SymbolEqualityComparer.Default.Equals(x.Base, y.Base) &&
                SymbolEqualityComparer.Default.Equals(x.Field, y.Field);

            public int GetHashCode((ITypeSymbol Base, ITypeSymbol Field) pair) =>
                SymbolEqualityComparer.Default.GetHashCode(pair.Base) * 397 ^
                SymbolEqualityComparer.Default.GetHashCode(pair.Field);
        }
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

    // A SerializableType / SerializableType<T> field names a Type (like a string) rather than instantiating one, so
    // [TypeSelector] is valid on it. Matched by the wrapper's original definition so both the non-generic and the
    // open-generic form are recognized; List<>/array are already unwrapped into the element type by the caller.
    private static bool IsSerializableType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;

        var definition = named.OriginalDefinition.ToDisplayString();
        return definition == AspidClasses.SerializableTypeFull ||
            definition == AspidClasses.SerializableTypeGenericFull;
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
