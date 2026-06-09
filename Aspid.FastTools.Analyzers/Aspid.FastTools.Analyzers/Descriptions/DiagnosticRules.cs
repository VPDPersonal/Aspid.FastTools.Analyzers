using Severity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using Descriptor = Microsoft.CodeAnalysis.DiagnosticDescriptor;

namespace Aspid.FastTools.Analyzers.Descriptions;

public static class DiagnosticRules
{
    private const string UsageCategory = "Usage";

    public static readonly Descriptor TypeSelectorFieldTypeRule = new(
        id: "AFT0001",
        title: "[TypeSelector] applied to an unsupported field",
        messageFormat: "[TypeSelector] on '{0}' has no effect: apply it to a string field or to a [SerializeReference] managed-reference field",
        category: UsageCategory,
        defaultSeverity: Severity.Error,
        isEnabledByDefault: true);

    public static readonly Descriptor TypeSelectorAllowRule = new(
        id: "AFT0002",
        title: "[TypeSelector] Allow has no effect on a managed reference",
        messageFormat: "[TypeSelector] on '{0}' sets Allow, but abstract classes and interfaces cannot be instantiated for a [SerializeReference] field — Allow is ignored here",
        category: UsageCategory,
        defaultSeverity: Severity.Warning,
        isEnabledByDefault: true);

    public static readonly Descriptor TypeSelectorBaseTypeRule = new(
        id: "AFT0003",
        title: "[TypeSelector] base type shares no concrete type with the field",
        messageFormat: "[TypeSelector] base type '{0}' shares no concrete type with the field type '{1}' — the selector will be empty",
        category: UsageCategory,
        defaultSeverity: Severity.Warning,
        isEnabledByDefault: true);
}
