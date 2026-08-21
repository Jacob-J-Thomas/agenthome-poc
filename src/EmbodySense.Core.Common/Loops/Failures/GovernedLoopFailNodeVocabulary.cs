namespace EmbodySense.Core.Common.Loops.Failures;

/// <summary>Defines the exact schema-1 descriptor and optional parameters for the canonical Fail terminal.</summary>
public static class GovernedLoopFailNodeVocabulary
{
    /// <summary>The exact Fail terminal type identifier.</summary>
    public const string TypeId = "fail-terminal";

    /// <summary>The exact descriptor version.</summary>
    public const int DescriptorVersion = 1;

    /// <summary>The optional bounded server-owned code for an explicit agent-selected failure.</summary>
    public const string CodeParameter = "code";

    /// <summary>The optional safe value-free explanation for an explicit agent-selected failure.</summary>
    public const string ExplanationParameter = "explanation";
}
