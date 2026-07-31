using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using System.Text;

namespace EmbodySense.Core.Common.Tests;

public sealed class ToolResultFormatterTests
{
    private static readonly string _expectedTruncationMarker = $"[formatted tool results truncated to the {ToolResultFormatter.MaxFormattedCharacters}-character limit]";

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
        Assert.EndsWith(_expectedTruncationMarker, formatted, StringComparison.Ordinal);
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
        const string OutputMarker = "    [tool output truncated to preserve all result evidence]";
        var baselineResult = CreateResult(new string('a', ToolResultFormatter.MaxFormattedCharacters * 2)) with
        {
            Retention = CreateRetention(characterCount: 0, utf8ByteCount: 0)
        };
        var baseline = ToolResultFormatter.FormatResults([baselineResult]);
        var outputTextStart = baseline.IndexOf("  output:" + Environment.NewLine + "    ", StringComparison.Ordinal) + ("  output:" + Environment.NewLine + "    ").Length;
        var outputMarkerStart = baseline.IndexOf(Environment.NewLine + OutputMarker, outputTextStart, StringComparison.Ordinal);
        var retainedOutputCharacterCount = outputMarkerStart - outputTextStart;
        var output = new string('a', retainedOutputCharacterCount - 1) + "\U0001F600" + new string('b', 1_000);
        var result = baselineResult with { OutputText = output };

        var formatted = ToolResultFormatter.FormatResults([result]);

        Assert.Equal(ToolResultFormatter.MaxFormattedCharacters - 1, formatted.Length);
        Assert.EndsWith(_expectedTruncationMarker, formatted, StringComparison.Ordinal);
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
        Assert.EndsWith(_expectedTruncationMarker, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResults_reserves_meaningful_output_for_every_result_before_expanding_flexible_metadata()
    {
        var firstEvidence = "first-result|" + new string('a', 240);
        var secondEvidence = "second-result|" + new string('b', 240);
        var oversizedMetadata = new string('m', ToolResultFormatter.MaxFormattedCharacters);
        var first = CreateResult(firstEvidence + new string('x', ToolResultFormatter.MaxFormattedCharacters)) with
        {
            ResolvedPath = oversizedMetadata,
            Request = new ToolRequest(ToolCommand.Read, oversizedMetadata),
            Retention = CreateRetention(characterCount: 0, utf8ByteCount: 0) with { Detail = oversizedMetadata }
        };
        var second = CreateResult(secondEvidence + new string('y', ToolResultFormatter.MaxFormattedCharacters)) with
        {
            RequestId = "request-2",
            ResolvedPath = oversizedMetadata,
            Request = new ToolRequest(ToolCommand.Read, oversizedMetadata),
            Retention = new ToolResultRetentionReference(ToolResultRetentionStatus.Unavailable, null, null, null, null, null, null, 0, oversizedMetadata)
        };

        var forward = ToolResultFormatter.FormatResults([first, second]);
        var reverse = ToolResultFormatter.FormatResults([second, first]);

        Assert.Equal(ToolResultFormatter.MaxFormattedCharacters, forward.Length);
        Assert.Equal(ToolResultFormatter.MaxFormattedCharacters, reverse.Length);
        Assert.Contains(firstEvidence, forward, StringComparison.Ordinal);
        Assert.Contains(secondEvidence, forward, StringComparison.Ordinal);
        Assert.Contains(firstEvidence, reverse, StringComparison.Ordinal);
        Assert.Contains(secondEvidence, reverse, StringComparison.Ordinal);
        Assert.Equal(2, forward.Split("[tool output truncated to preserve all result evidence]", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, reverse.Split("[tool output truncated to preserve all result evidence]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void FormatResults_keeps_essential_identifiers_and_retention_integrity_references_complete()
    {
        var requestId = "request-" + new string('q', 4_096);
        var manifestPath = ".agent/logs/tool-responses/" + new string('m', 16_000) + "/manifest.json";
        var contentSha256 = new string('b', 64);
        var oversizedMetadata = new string('x', ToolResultFormatter.MaxFormattedCharacters);
        var result = CreateResult(new string('o', ToolResultFormatter.MaxFormattedCharacters)) with
        {
            RequestId = requestId,
            ResolvedPath = oversizedMetadata,
            Request = new ToolRequest(ToolCommand.Read, oversizedMetadata),
            Retention = CreateRetention(characterCount: 0, utf8ByteCount: 0) with
            {
                ManifestPath = manifestPath,
                ContentSha256 = contentSha256,
                Detail = oversizedMetadata
            }
        };

        var formatted = ToolResultFormatter.FormatResults([result]);

        Assert.Contains($"- request_id: {requestId}{Environment.NewLine}", formatted, StringComparison.Ordinal);
        Assert.Contains($"  full_response_manifest: {manifestPath}{Environment.NewLine}", formatted, StringComparison.Ordinal);
        Assert.Contains($"  full_response_sha256: {contentSha256}{Environment.NewLine}", formatted, StringComparison.Ordinal);
        Assert.EndsWith(_expectedTruncationMarker, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResults_rejects_an_envelope_that_would_require_truncating_a_request_id()
    {
        var result = CreateResult("output") with { RequestId = new string('r', ToolResultFormatter.MaxFormattedCharacters) };

        var exception = Assert.Throws<ArgumentException>(() => ToolResultFormatter.FormatResults([result]));

        Assert.Contains("minimum evidence envelope", exception.Message, StringComparison.Ordinal);
        Assert.Equal("results", exception.ParamName);
    }

    [Fact]
    public void FormatResults_rejects_an_envelope_that_would_require_truncating_a_retained_manifest_path()
    {
        var result = CreateResult("output") with
        {
            Retention = CreateRetention(characterCount: 0, utf8ByteCount: 0) with
            {
                ManifestPath = new string('m', ToolResultFormatter.MaxFormattedCharacters)
            }
        };

        var exception = Assert.Throws<ArgumentException>(() => ToolResultFormatter.FormatResults([result]));

        Assert.Contains("minimum evidence envelope", exception.Message, StringComparison.Ordinal);
        Assert.Equal("results", exception.ParamName);
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
        Assert.EndsWith(_expectedTruncationMarker, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResults_preserves_evidence_for_every_result_before_sharing_output_budget()
    {
        var first = CreateResult(new string('a', ToolResultFormatter.MaxFormattedCharacters * 2));
        var second = CreateResult(new string('b', ToolResultFormatter.MaxFormattedCharacters * 2)) with
        {
            RequestId = "request-2",
            ResolvedPath = "C:\\workspace\\private\\secret.txt",
            Request = new ToolRequest(ToolCommand.Read, "private/secret.txt"),
            Retention = new ToolResultRetentionReference(ToolResultRetentionStatus.Unavailable, null, null, null, null, null, null, 0, "retention failed closed")
        };

        var formatted = ToolResultFormatter.FormatResults([first, second]);

        Assert.Equal(ToolResultFormatter.MaxFormattedCharacters, formatted.Length);
        Assert.Contains("- request_id: request-1", formatted, StringComparison.Ordinal);
        Assert.Contains("full_response_manifest: .agent/logs/tool-responses/request-1/manifest.json", formatted, StringComparison.Ordinal);
        Assert.Contains("target_path: shared/note.txt", formatted, StringComparison.Ordinal);
        Assert.Contains("resolved_path: C:\\workspace\\shared\\note.txt", formatted, StringComparison.Ordinal);
        Assert.Contains("- request_id: request-2", formatted, StringComparison.Ordinal);
        Assert.Contains("outcome: succeeded", formatted, StringComparison.Ordinal);
        Assert.Contains("full_response_manifest: unavailable", formatted, StringComparison.Ordinal);
        Assert.Contains("full_response_retention: retention failed closed", formatted, StringComparison.Ordinal);
        Assert.Contains("target_path: private/secret.txt", formatted, StringComparison.Ordinal);
        Assert.Contains("resolved_path: C:\\workspace\\private\\secret.txt", formatted, StringComparison.Ordinal);
        Assert.Equal(2, formatted.Split("[tool output truncated to preserve all result evidence]", StringSplitOptions.None).Length - 1);
        Assert.Contains("Use these results to continue the task.", formatted, StringComparison.Ordinal);
        Assert.EndsWith(_expectedTruncationMarker, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResults_rejects_a_result_count_whose_minimum_evidence_cannot_fit()
    {
        var results = Enumerable.Range(0, 1_000).Select(index => CreateResult("") with { RequestId = $"request-{index}" }).ToArray();

        var exception = Assert.Throws<ArgumentException>(() => ToolResultFormatter.FormatResults(results));

        Assert.Contains("minimum evidence envelope", exception.Message, StringComparison.Ordinal);
        Assert.Equal("results", exception.ParamName);
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
