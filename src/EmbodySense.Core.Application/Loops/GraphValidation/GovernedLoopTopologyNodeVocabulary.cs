namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Defines the closed schema-1 executable Condition and Join descriptor vocabulary.</summary>
public static class GovernedLoopTopologyNodeVocabulary
{
    /// <summary>The only supported topology descriptor version.</summary>
    public const int DescriptorVersion = 1;
    /// <summary>A Condition that selects from an exact non-null Boolean input.</summary>
    public const string BooleanCondition = "boolean-condition";
    /// <summary>A Condition that compares bounded text with one exact expected value.</summary>
    public const string ExactTextCondition = "exact-text-condition";
    /// <summary>A Condition that accepts only one of two exact model-decision values.</summary>
    public const string ModelDecisionCondition = "model-decision-condition";
    /// <summary>A Join satisfied by every declared incoming control edge.</summary>
    public const string AllJoin = "all-join";
    /// <summary>A Join satisfied by the first durable incoming control edge.</summary>
    public const string AnyJoin = "any-join";
    /// <summary>A Join satisfied by all branch-selected arrivals while excluding proven skipped paths.</summary>
    public const string SelectedJoin = "selected-join";
    /// <summary>The exact Boolean input port.</summary>
    public const string ValuePort = "value";
    /// <summary>The exact model-decision input port.</summary>
    public const string DecisionPort = "decision";
    /// <summary>The expected value used by exact-text comparison.</summary>
    public const string ExpectedParameter = "expected";
    /// <summary>The exact model-decision value selecting the True path.</summary>
    public const string TrueDecisionParameter = "true-decision";
    /// <summary>The exact model-decision value selecting the False path.</summary>
    public const string FalseDecisionParameter = "false-decision";
    /// <summary>The explicit positive iteration budget required by cycle-capable Conditions.</summary>
    public const string MaximumIterationsParameter = "max-iterations";
    /// <summary>The explicit positive wall-clock budget required by cycle-capable Conditions.</summary>
    public const string MaximumDurationMillisecondsParameter = "max-duration-milliseconds";

    /// <summary>Gets the six exact descriptor type identities in canonical order.</summary>
    public static IReadOnlyList<string> DescriptorTypeIds { get; } = Array.AsReadOnly(new[]
    {
        AllJoin,
        AnyJoin,
        BooleanCondition,
        ExactTextCondition,
        ModelDecisionCondition,
        SelectedJoin,
    });

    /// <summary>Gets whether one exact identifier names a supported Condition.</summary>
    public static bool IsCondition(string? typeId)
        => typeId is BooleanCondition or ExactTextCondition or ModelDecisionCondition;

    /// <summary>Gets whether one exact identifier names a supported Join.</summary>
    public static bool IsJoin(string? typeId)
        => typeId is AllJoin or AnyJoin or SelectedJoin;
}
