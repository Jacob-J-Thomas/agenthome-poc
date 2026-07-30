using EmbodySense.Core.Common.Loops.Models.Custom;
namespace EmbodySense.Core.Common.Loops.Custom;

public sealed record CustomLoopValidationResult(IReadOnlyList<CustomLoopValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
