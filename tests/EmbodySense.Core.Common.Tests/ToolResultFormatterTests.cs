using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using System.Text;

namespace EmbodySense.Core.Common.Tests;

public sealed class ToolResultFormatterTests
{
    private static readonly string ExpectedTruncationMarker = $"[formatted tool results truncated to the {ToolResultFormatter.MaxFormattedCharacters}-character limit]";

    [Fact]
    public void FormatResults_preserves_the_exact_ordinary_format_when_it_fits()
    {
        var result = CreateResult("first line\nsecond line");

        var formatted = ToolResultFormatter.FormatResults([result]);

        var expected = string.Join(Environment.NewLine,
        [
            "EmbodySense dynamic tool results:",
            "- request_id: request-1",
            "  tool: read",
            "  outcome: succeeded",
            "  full_response_manifest: .agent/logs/tool-responses/request-1/manifest.json",
            "  full_response_sha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "  full_response_size: 22 characters / 22 UTF-8 bytes / 1 chunks",
            "  full_response_retention: retained for test",
            "  target_path: shared/note.txt",
            "  resolved_path: C:\\workspace\\shared\\note.txt",
            "  output:",
            "    first line",
            "    second line",
            "Use these results to continue the task. Request another dynamic tool only if needed."
        ]);
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void FormatResults_caps_the_final_string_after_newline_indentation_amplification()
    {
        var newlineHeavyOutput = string.Concat(Enumerable.Repeat("x\n", 20_000));

        var formatted = ToolResultFormatter.FormatResults([CreateResult(newlineHeavyOutput)]);

        Assert.Equal(ToolResultFormatter.MaxFormattedCharacters, formatted.Length);
        Assert.EndsWith(ExpectedTruncationMarker, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResults_has_a_stable_maximum_for_escape_heavy_output()
    {
        var escapeHeavyOutput = string.Concat(Enumerable.Repeat("\\\"\r\n", 30_000));
        var result = CreateResult(escapeHeavyOutput);

        var first = ToolResultFormatter.FormatResults([result]);
        var second = ToolResultFormatter.FormatResults([result]);

        Assert.Equal(ToolResultFormatter.MaxFormattedCharacters, first.Length);
        Assert.Equal(first, second);
        Assert.Contains("formatted tool results truncated", first, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResults_backs_off_instead_of_splitting_a_surrogate_pair_at_the_final_boundary()
    {
        var marker = Environment.NewLine + ExpectedTruncationMarker;
        var retainedCharacterCount = ToolResultFormatter.MaxFormattedCharacters - marker.Length;
        var outputPrefix = string.Join(Environment.NewLine,
        [
            "EmbodySense dynamic tool results:",
            "- request_id: request-1",
            "  tool: read",
            "  outcome: succeeded",
            "  full_response_manifest: .agent/logs/tool-responses/request-1/manifest.json",
            "  full_response_sha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "  full_response_size: 0 characters / 0 UTF-8 bytes / 1 chunks",
            "  full_response_retention: retained for test",
            "  target_path: shared/note.txt",
            "  resolved_path: C:\\workspace\\shared\\note.txt",
            "  output:",
            "    "
        ]);
        var fillerLength = retainedCharacterCount - outputPrefix.Length - 1;
        var output = new string('a', fillerLength) + "\U0001F600" + new string('b', 1_000);

        var result = CreateResult(output) with
        {
            Retention = CreateRetention(characterCount: 0, utf8ByteCount: 0)
        };
        var formatted = ToolResultFormatter.FormatResults([result]);

        Assert.Equal(ToolResultFormatter.MaxFormattedCharacters - 1, formatted.Length);
        Assert.EndsWith(ExpectedTruncationMarker, formatted, StringComparison.Ordinal);
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(formatted);
    }

    [Fact]
    public void FormatResults_surfaces_an_unavailable_full_response_before_truncated_content()
    {
        var result = CreateResult(new string('x', ToolResultFormatter.MaxFormattedCharacters * 2)) with
        {
            Retention = new ToolResultRetentionReference(ToolResultRetentionStatus.Unavailable, null, null, null, null, null, null, 0, "retention failed closed")
        };

        var formatted = ToolResultFormatter.FormatResults([result]);

        Assert.Contains("full_response_manifest: unavailable", formatted, StringComparison.Ordinal);
        Assert.Contains("retention failed closed", formatted, StringComparison.Ordinal);
        Assert.EndsWith(ExpectedTruncationMarker, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResults_preserves_retention_references_before_untrusted_paths_consume_the_limit()
    {
        var result = CreateResult("complete") with
        {
            ResolvedPath = new string('r', ToolResultFormatter.MaxFormattedCharacters),
            Request = new ToolRequest(ToolCommand.Read, new string('t', ToolResultFormatter.MaxFormattedCharacters))
        };

        var formatted = ToolResultFormatter.FormatResults([result]);

        Assert.Contains("full_response_manifest: .agent/logs/tool-responses/request-1/manifest.json", formatted, StringComparison.Ordinal);
        Assert.Contains("full_response_sha256: " + new string('a', 64), formatted, StringComparison.Ordinal);
        Assert.EndsWith(ExpectedTruncationMarker, formatted, StringComparison.Ordinal);
    }

    private static ToolResult CreateResult(string output)
    {
        return new ToolResult(
            ToolExecutionOutcome.Succeeded,
            output,
            "request-1",
            "C:\\workspace\\shared\\note.txt",
            new ToolRequest(ToolCommand.Read, "shared/note.txt"),
            Retention: CreateRetention(output.Length, Encoding.UTF8.GetByteCount(output)));
    }

    private static ToolResultRetentionReference CreateRetention(int characterCount, long utf8ByteCount)
    {
        return new ToolResultRetentionReference(
            ToolResultRetentionStatus.Retained,
            ".agent/logs/tool-responses/request-1/manifest.json",
            new string('a', 64),
            characterCount,
            utf8ByteCount,
            1,
            DateTimeOffset.UnixEpoch,
            0,
            "retained for test");
    }
}
