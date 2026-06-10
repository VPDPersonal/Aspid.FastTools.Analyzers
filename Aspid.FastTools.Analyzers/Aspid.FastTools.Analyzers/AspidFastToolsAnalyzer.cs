using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Aspid.FastTools.Analyzers.Descriptions;
using UnityAttributes = Aspid.FastTools.Analyzers.Descriptions.UnityEngine.AttributesDescription;
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
            DiagnosticRules.TypeSelectorBaseTypeRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeField, SyntaxKind.FieldDeclaration);
    }

    private static void AnalyzeField(SyntaxNodeAnalysisContext context)
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
        ReportDisjointBaseTypes(context, typeSelector, elementType);
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
    private static void ReportDisjointBaseTypes(SyntaxNodeAnalysisContext context, AttributeSyntax typeSelector, ITypeSymbol fieldElementType)
    {
        if (typeSelector.ArgumentList is null) return;

        foreach (var argument in typeSelector.ArgumentList.Arguments)
        {
            if (argument.NameEquals is not null) continue;                          // skip Allow = ...
            if (argument.Expression is not TypeOfExpressionSyntax typeOf) continue;  // only typeof(...) args are statically checkable

            if (context.SemanticModel.GetTypeInfo(typeOf.Type).Type is not { } baseType) continue;
            if (baseType.SpecialType == SpecialType.System_Object) continue;         // the unconstrained default narrows nothing

            if (AreProvablyDisjoint(baseType, fieldElementType))
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticRules.TypeSelectorBaseTypeRule, typeOf.GetLocation(), baseType.Name, fieldElementType.Name));
        }
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
