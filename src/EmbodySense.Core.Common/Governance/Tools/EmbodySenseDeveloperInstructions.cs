using System.Text;
using System.Security.Cryptography;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Governance.Tools;

/// <summary>
/// Captures, matches, and composes EmbodySense developer instructions.
/// </summary>
public static class EmbodySenseDeveloperInstructions
{
    /// <summary>
    /// Version identity for the fixed governed app-server instruction contract.
    /// </summary>
    public const string CurrentVersion = "codex-app-server-governance-v2";

    /// <summary>
    /// Creates the fixed governance instructions for the commands assigned to a model turn.
    /// </summary>
    /// <param name="availableToolCommands">The commands admitted for the active loop turn; duplicates are removed and values are ordered canonically.</param>
    /// <returns>Instructions that prohibit native workspace tools and expose only the admitted <c>embodysense.command</c> capabilities.</returns>
    public static string Create(IReadOnlyList<ToolCommand>? availableToolCommands = null)
    {
        var commands = (availableToolCommands ?? [])
            .Distinct()
            .Order()
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("""
            You are running inside EmbodySense through the Codex app-server protocol.

            EmbodySense governs the user workspace. Do not use Codex-native shell, filesystem, MCP, browser, web-search, subagent, or permission-escalation tools for workspace actions. The app-server working directory is an inert runtime directory, not the user workspace.
            """);

        if (commands.Length == 0)
        {
            builder.AppendLine();
            builder.AppendLine("The active EmbodySense loop has not assigned any workspace command capabilities to this turn. Do not perform workspace actions, and do not claim a workspace action succeeded.");
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine($"The active EmbodySense loop assigned these workspace command capabilities to this turn: {string.Join(", ", commands.Select(ToolCommandFormatter.Format))}.");
            builder.AppendLine("For assigned workspace actions, use only the `embodysense.command` dynamic tool. It enforces loop capability filtering, `.agent/permissions.json`, approval routing, and audit logging. Do not request unassigned workspace commands, and do not claim a workspace action succeeded until the corresponding EmbodySense tool result says it succeeded.");
            if (commands.Any(command => command is ToolCommand.Append or ToolCommand.Write or ToolCommand.Delete))
            {
                builder.AppendLine("Append, write, and delete accept only the tool's closed schema-1 `input` object. Set `scopeId` to `workspace`, make `target` exactly match `path`, select one optimistic precondition, and use bounded ordered `segments` (empty only for delete). Do not send legacy raw mutation content, absolute/private/wildcard/recursive targets, or secret values; credential references remain value-free and may fail closed when no trusted lease bridge is available.");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Captures the current EmbodySense developer instruction set.
    /// </summary>
    /// <param name="availableToolCommands">The commands admitted for the active loop turn.</param>
    /// <returns>An immutable instruction snapshot containing version, content, and lowercase SHA-256 content hash.</returns>
    public static EmbodySenseDeveloperInstructionSet Capture(IReadOnlyList<ToolCommand>? availableToolCommands = null)
    {
        var content = Create(availableToolCommands);
        return new EmbodySenseDeveloperInstructionSet(CurrentVersion, content, ComputeHash(content));
    }

    /// <summary>
    /// Determines whether the candidate matches the expected EmbodySense developer instructions.
    /// </summary>
    /// <param name="candidate">The captured instruction set to verify.</param>
    /// <param name="availableToolCommands">The commands from which the expected snapshot is reconstructed.</param>
    /// <returns><see langword="true"/> when version, content, and content hash exactly match the reconstructed snapshot; otherwise, <see langword="false"/>.</returns>
    public static bool Matches(EmbodySenseDeveloperInstructionSet? candidate, IReadOnlyList<ToolCommand>? availableToolCommands = null)
    {
        if (candidate is null)
        {
            return false;
        }

        var expected = Capture(availableToolCommands);
        return string.Equals(candidate.Version, expected.Version, StringComparison.Ordinal)
            && string.Equals(candidate.Content, expected.Content, StringComparison.Ordinal)
            && FixedTimeEquals(candidate.ContentHash, expected.ContentHash);
    }

    /// <summary>
    /// Composes the EmbodySense developer instructions.
    /// </summary>
    /// <param name="governance">The unaltered fixed governance snapshot that must remain first and authoritative.</param>
    /// <param name="trustedInstructions">The ordered trusted workspace instruction blocks to append with source boundaries.</param>
    /// <returns>The fixed governance content followed by each explicitly delimited trusted instruction block.</returns>
    /// <exception cref="ArgumentNullException">Thrown when either argument or an instruction element is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when governance or trusted instruction content is incomplete, empty, or hash-mismatched.</exception>
    public static string Compose(EmbodySenseDeveloperInstructionSet governance, IReadOnlyList<EmbodySenseTrustedInstruction> trustedInstructions)
    {
        ArgumentNullException.ThrowIfNull(governance);
        ArgumentNullException.ThrowIfNull(trustedInstructions);
        if (string.IsNullOrWhiteSpace(governance.Version) || string.IsNullOrWhiteSpace(governance.Content) || !FixedTimeEquals(governance.ContentHash, ComputeHash(governance.Content)))
        {
            throw new ArgumentException("The fixed EmbodySense governance instruction snapshot is incomplete or has been altered.", nameof(governance));
        }

        if (trustedInstructions.Count == 0)
        {
            return governance.Content;
        }

        var builder = new StringBuilder(governance.Content);
        foreach (var instruction in trustedInstructions)
        {
            ArgumentNullException.ThrowIfNull(instruction);
            ArgumentException.ThrowIfNullOrWhiteSpace(instruction.SourceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(instruction.Content);
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"[EmbodySense trusted instruction source: {instruction.SourceId}]");
            builder.AppendLine(instruction.Content);
            builder.Append($"[/EmbodySense trusted instruction source: {instruction.SourceId}]");
        }

        return builder.ToString();
    }

    private static string ComputeHash(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string? left, string? right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.ASCII.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
