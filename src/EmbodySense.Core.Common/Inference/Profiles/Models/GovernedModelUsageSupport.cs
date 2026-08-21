namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Describes whether one provider-usage dimension is measurable and enforceable.</summary>
public enum GovernedModelUsageSupport
{
    /// <summary>The dimension is explicitly unavailable.</summary>
    Unavailable = 0,
    /// <summary>The dimension is authoritatively reported only after dispatch.</summary>
    AuthoritativeAfterDispatch = 1,
    /// <summary>The dimension is authoritatively reported and can be hard-bounded before dispatch.</summary>
    AuthoritativeAndHardBoundedAtDispatch = 2
}
