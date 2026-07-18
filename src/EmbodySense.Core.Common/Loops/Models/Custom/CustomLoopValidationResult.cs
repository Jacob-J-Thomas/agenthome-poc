namespace EmbodySense.Core.Common.Loops.Models.Custom;

public sealed record CustomLoopValidationResult(IReadOnlyList<CustomLoopValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
