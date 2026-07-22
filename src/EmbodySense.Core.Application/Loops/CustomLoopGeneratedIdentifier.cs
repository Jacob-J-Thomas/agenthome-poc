namespace EmbodySense.Core.Application.Loops;

internal static class CustomLoopGeneratedIdentifier
{
    internal static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
