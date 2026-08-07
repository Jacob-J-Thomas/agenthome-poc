namespace EmbodySense.Core.Common.Loops.Custom.Graph;

using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

internal sealed class GovernedLoopGraphErrorCollector
{
    private readonly List<GovernedLoopGraphValidationError> _errors = [];

    public bool Any => _errors.Count > 0;

    public void Add(string code, GovernedLoopGraphElementKind kind, string? id, string path, string message)
    {
        _errors.Add(new GovernedLoopGraphValidationError(Truncate(code, CustomLoopLimits.MaxGraphValidationErrorCodeCharacters), new GovernedLoopGraphElementReference(kind, NormalizeId(id), Truncate(path, CustomLoopLimits.MaxGraphValidationErrorPathCharacters)), Truncate(message, CustomLoopLimits.MaxGraphValidationErrorMessageCharacters)));
    }

    public IReadOnlyList<GovernedLoopGraphValidationError> ToSortedErrors()
    {
        return Array.AsReadOnly(_errors.OrderBy(error => error.Element.Path, StringComparer.Ordinal).ThenBy(error => error.Code, StringComparer.Ordinal).ThenBy(error => error.Element.Id, StringComparer.Ordinal).Take(CustomLoopLimits.MaxGraphValidationErrors).ToArray());
    }

    private static string Truncate(string? value, int maximum)
    {
        return string.IsNullOrEmpty(value) ? "invalid" : value.Length <= maximum ? value : value[..maximum];
    }

    private static string? NormalizeId(string? value)
    {
        return value is null ? null : Truncate(value, CustomLoopLimits.MaxArtifactIdCharacters);
    }
}
