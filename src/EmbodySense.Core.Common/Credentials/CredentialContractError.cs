using System.Diagnostics;
using System.Text;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials;

/// <summary>Describes one bounded, value-free credential contract rejection.</summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class CredentialContractError
{
    private CredentialContractError(CredentialContractErrorCode code, string path)
    {
        Code = code;
        Path = path;
        CanonicalCode = ToCanonical(code);
        Message = $"Credential contract rejected: {CanonicalCode}.";
    }

    /// <summary>Gets the closed rejection category.</summary>
    public CredentialContractErrorCode Code { get; }

    /// <summary>Gets the canonical snake-case rejection token.</summary>
    public string CanonicalCode { get; }

    /// <summary>Gets the bounded safe field path.</summary>
    public string Path { get; }

    /// <summary>Gets a fixed value-free message derived only from the closed category.</summary>
    public string Message { get; }

    private string DebuggerDisplay => ToString();

    internal static CredentialContractError Create(CredentialContractErrorCode code, string? path)
    {
        var supportedCode = code != CredentialContractErrorCode.Unknown && Enum.IsDefined(code) ? code : CredentialContractErrorCode.InvalidCredentialJson;
        return new CredentialContractError(supportedCode, IsSafePath(path) ? path! : "$");
    }

    /// <inheritdoc />
    public override string ToString() => $"{CanonicalCode} at {Path}";

    private static bool IsSafePath(string? path)
    {
        return path is not null && path.Length is > 0 and <= CredentialContractLimits.MaxErrorPathCharacters && path[0] == '$' && path.All(character => character is '$' or '.' or '-' or '_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');
    }

    private static string ToCanonical(CredentialContractErrorCode code)
    {
        var name = code.ToString();
        var builder = new StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[index]));
        }

        return builder.ToString();
    }
}
