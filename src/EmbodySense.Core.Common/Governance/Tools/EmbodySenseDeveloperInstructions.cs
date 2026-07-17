using System.Text;
using System.Security.Cryptography;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Governance.Tools;

public sealed record EmbodySenseDeveloperInstructionSet(
    string Version,
    string Content,
    string ContentHash);

public sealed record EmbodySenseTrustedInstruction(
    string SourceId,
    string Content);

public static class EmbodySenseDeveloperInstructions
{
    public const string CurrentVersion = "codex-app-server-governance-v1";

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
        }

        return builder.ToString().TrimEnd();
    }

    public static EmbodySenseDeveloperInstructionSet Capture(IReadOnlyList<ToolCommand>? availableToolCommands = null)
    {
        var content = Create(availableToolCommands);
        return new EmbodySenseDeveloperInstructionSet(CurrentVersion, content, ComputeHash(content));
    }

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
