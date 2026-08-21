namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Contains exact process tokens derived only from one registered template and validated input.</summary>
/// <param name="Arguments">The complete ordered argument tokens.</param>
/// <param name="Environment">The complete fixed non-secret environment.</param>
/// <param name="StandardInputUtf8">The exact optional standard-input value.</param>
/// <param name="InputFingerprint">The canonical input fingerprint.</param>
public sealed record CommandActionMaterialization(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? StandardInputUtf8,
    string InputFingerprint)
{
    /// <summary>Gets a defensive immutable copy of the argument tokens.</summary>
    public IReadOnlyList<string> Arguments { get; } = Arguments is null ? null! : Array.AsReadOnly(Arguments.ToArray());
    /// <summary>Gets a defensive immutable copy of the environment values.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; } = Environment is null
        ? null!
        : new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Environment, StringComparer.Ordinal));
}
