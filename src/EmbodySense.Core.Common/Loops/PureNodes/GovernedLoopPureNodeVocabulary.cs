using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Defines the exact initial descriptor, port, parameter, and value-kind vocabulary for deterministic pure nodes.</summary>
public static class GovernedLoopPureNodeVocabulary
{
    /// <summary>The only supported pure-node descriptor version.</summary>
    public const int DescriptorVersion = 1;
    /// <summary>The exact identity transform descriptor.</summary>
    public const string IdentityTransform = "identity-transform";
    /// <summary>The exact RFC 6901 structured-selection transform descriptor.</summary>
    public const string StructuredSelect = "structured-select";
    /// <summary>The exact bounded ordered text-concatenation transform descriptor.</summary>
    public const string OrderedTextConcat = "ordered-text-concat";
    /// <summary>The exact structural-schema attestation descriptor for already admitted typed bindings.</summary>
    public const string SchemaConformance = "schema-conformance";
    /// <summary>The exact canonical equality validator descriptor.</summary>
    public const string CanonicalEquality = "canonical-equality";
    /// <summary>The exact inclusive signed-integer range validator descriptor.</summary>
    public const string InclusiveIntegerRange = "inclusive-integer-range";
    /// <summary>The exact inclusive finite-number range validator descriptor.</summary>
    public const string InclusiveNumberRange = "inclusive-number-range";
    /// <summary>The exact text-length validator descriptor.</summary>
    public const string TextLength = "text-length";
    /// <summary>The exact array-length validator descriptor.</summary>
    public const string ArrayLength = "array-length";
    /// <summary>The conventional single-value input port.</summary>
    public const string InputPort = "input";
    /// <summary>The canonical-equality left input port.</summary>
    public const string LeftPort = "left";
    /// <summary>The canonical-equality right input port.</summary>
    public const string RightPort = "right";
    /// <summary>The ordered text values input port.</summary>
    public const string ValuesPort = "values";
    /// <summary>The conventional transformed output port.</summary>
    public const string OutputPort = "output";
    /// <summary>The conventional Boolean validation-result output port.</summary>
    public const string ResultPort = "result";
    /// <summary>The RFC 6901 pointer parameter.</summary>
    public const string PointerParameter = "pointer";
    /// <summary>The ordered text separator parameter.</summary>
    public const string SeparatorParameter = "separator";
    /// <summary>The inclusive lower-bound parameter.</summary>
    public const string MinimumParameter = "minimum";
    /// <summary>The inclusive upper-bound parameter.</summary>
    public const string MaximumParameter = "maximum";

    private static readonly string[] _descriptorTypeIds =
    [
        ArrayLength,
        CanonicalEquality,
        IdentityTransform,
        InclusiveIntegerRange,
        InclusiveNumberRange,
        OrderedTextConcat,
        SchemaConformance,
        StructuredSelect,
        TextLength
    ];

    /// <summary>Gets every exact initial descriptor identity in ordinal order.</summary>
    /// <value>The immutable closed descriptor catalog.</value>
    public static IReadOnlyList<string> DescriptorTypeIds => Array.AsReadOnly(_descriptorTypeIds);

    /// <summary>Creates the exact non-Binary value-kind set admitted by pure nodes.</summary>
    /// <returns>A new immutable kind-set value.</returns>
    public static GovernedLoopValueKindSet PureValueKinds() => GovernedLoopValueKindSet.Create(
    [
        GovernedLoopValueKind.Text,
        GovernedLoopValueKind.Boolean,
        GovernedLoopValueKind.Integer,
        GovernedLoopValueKind.Number,
        GovernedLoopValueKind.Object,
        GovernedLoopValueKind.Array
    ]);

    /// <summary>Returns whether a descriptor is one of the closed schema-1 transform operators.</summary>
    /// <param name="typeId">The exact descriptor identity.</param>
    /// <returns><see langword="true"/> only for an initial transform identity.</returns>
    public static bool IsTransform(string? typeId) => typeId is IdentityTransform or StructuredSelect or OrderedTextConcat;

    /// <summary>Returns whether a descriptor is one of the closed schema-1 validation operators.</summary>
    /// <param name="typeId">The exact descriptor identity.</param>
    /// <returns><see langword="true"/> only for an initial validation identity.</returns>
    public static bool IsValidate(string? typeId) => typeId is SchemaConformance or CanonicalEquality or InclusiveIntegerRange or InclusiveNumberRange or TextLength or ArrayLength;
}
