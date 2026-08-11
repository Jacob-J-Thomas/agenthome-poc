namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

/// <summary>Returns a bounded immutable snapshot of Human Input request lifecycle validation errors.</summary>
public sealed partial record HumanInputRequestLifecycleValidationResult
{
    /// <summary>Gets the immutable error snapshot.</summary>
    public IReadOnlyList<HumanInputRequestLifecycleValidationError> Errors { get; }
}
