namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Returns a bounded immutable snapshot of authenticated-response contract validation errors.</summary>
public sealed partial record HumanInputResponseValidationResult
{
    /// <summary>Gets the immutable error snapshot.</summary>
    public IReadOnlyList<HumanInputResponseValidationError> Errors { get; }
}
