namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Describes one bounded structured admission-contract validation error without retaining hostile input.</summary>
/// <param name="Code">The stable error classification.</param>
/// <param name="Path">The bounded schema-relative field path.</param>
public sealed record GovernedLoopAdmissionValidationError(GovernedLoopAdmissionValidationErrorCode Code, string Path);
