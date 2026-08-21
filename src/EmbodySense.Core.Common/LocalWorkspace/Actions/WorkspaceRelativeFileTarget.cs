using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Common.LocalWorkspace.Actions;

/// <summary>Contains one NFC-normalized, exact workspace-relative regular-file target.</summary>
public sealed record WorkspaceRelativeFileTarget
{
    private static readonly HashSet<string> _reservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "clock$", "conin$", "conout$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "com¹", "com²", "com³",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        "lpt¹", "lpt²", "lpt³",
    };

    private WorkspaceRelativeFileTarget(string value, int depth)
    {
        Value = value;
        Depth = depth;
    }

    /// <summary>Gets the canonical slash-separated relative target.</summary>
    public string Value { get; }

    /// <summary>Gets the number of canonical target segments.</summary>
    public int Depth { get; }

    /// <summary>Gets an immutable copy of the canonical target segments.</summary>
    public IReadOnlyList<string> Segments => Array.AsReadOnly(Value.Split('/', StringSplitOptions.None));

    /// <summary>Parses one portable exact file target and rejects host aliases, private runtime paths, globs, and traversal.</summary>
    public static bool TryParse(string? value, out WorkspaceRelativeFileTarget? target, out string? reasonCode)
    {
        target = null;
        reasonCode = "workspace-target-invalid";
        if (string.IsNullOrEmpty(value)
            || value.Length > WorkspaceActionContractLimits.MaxTargetCharacters
            || value[0] is '/' or '\\'
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal)
            || value.Contains('*', StringComparison.Ordinal)
            || value.Contains('?', StringComparison.Ordinal)
            || Path.IsPathRooted(value))
        {
            return false;
        }

        var rawSegments = value.Split('/', StringSplitOptions.None);
        if (rawSegments.Length is < 1 or > WorkspaceActionContractLimits.MaxTargetSegments)
        {
            reasonCode = "workspace-target-depth-invalid";
            return false;
        }

        var normalized = new string[rawSegments.Length];
        for (var index = 0; index < rawSegments.Length; index++)
        {
            var segment = rawSegments[index];
            if (segment.Length is < 1 or > WorkspaceActionContractLimits.MaxTargetSegmentCharacters
                || segment is "." or ".."
                || segment.EndsWith(" ", StringComparison.Ordinal)
                || segment.EndsWith(".", StringComparison.Ordinal)
                || segment.Any(IsUnsafeCharacter))
            {
                reasonCode = "workspace-target-segment-invalid";
                return false;
            }

            var nfc = segment.Normalize(NormalizationForm.FormC);
            if (!string.Equals(segment, nfc, StringComparison.Ordinal)
                || nfc.Length > WorkspaceActionContractLimits.MaxTargetSegmentCharacters
                || IsReservedHostName(nfc))
            {
                reasonCode = "workspace-target-alias-invalid";
                return false;
            }
            normalized[index] = nfc;
        }

        if (string.Equals(normalized[0], ".agent", StringComparison.OrdinalIgnoreCase))
        {
            reasonCode = "workspace-target-private";
            return false;
        }

        var canonical = string.Join('/', normalized);
        if (canonical.Length > WorkspaceActionContractLimits.MaxTargetCharacters)
        {
            reasonCode = "workspace-target-too-large";
            return false;
        }

        target = new WorkspaceRelativeFileTarget(canonical, normalized.Length);
        reasonCode = null;
        return true;
    }

    private static bool IsReservedHostName(string segment)
    {
        var stem = segment.Split('.', 2, StringSplitOptions.None)[0].TrimEnd(' ', '.');
        return _reservedWindowsNames.Contains(stem);
    }

    private static bool IsUnsafeCharacter(char character)
        => char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            || character is '<' or '>' or '"' or '|' or '\0';

    /// <inheritdoc />
    public override string ToString() => Value;
}
