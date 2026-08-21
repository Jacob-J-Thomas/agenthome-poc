using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Common.Tests.CommandActions;

public sealed class CommandActionContractTests
{
    [Fact]
    public void Template_is_hashed_defensively_and_binds_every_process_control()
    {
        var slots = Slots().ToList();
        var arguments = Arguments().ToList();
        var environment = EnvironmentEntries().ToList();
        var template = Template(slots, arguments, environment);

        slots.Clear();
        arguments.Clear();
        environment.Clear();

        Assert.Null(CommandActionTemplateContract.Validate(template));
        Assert.Equal(6, template.Slots.Count);
        Assert.Equal(6, template.Arguments.Count);
        Assert.Equal(2, template.Environment.Count);
        Assert.Equal(CommandActionTemplateContract.Compute(template), template.ContentHash);
        Assert.NotEqual(template.ContentHash, CommandActionTemplateContract.Compute(template with { TemplateVersion = 2, ContentHash = string.Empty }));
        Assert.Equal("command-template-content-hash-mismatch", CommandActionTemplateContract.Validate(template with { TemplateVersion = 2 }));
    }

    [Fact]
    public void Input_materializes_literal_tokens_without_shell_composition_or_ambient_environment()
    {
        var template = Template();
        var input = Input(template, text: "space ; && $(literal) \"quote\" Ω 😀");
        var encoded = CommandActionInputContract.Encode(input, template);

        Assert.True(CommandActionInputContract.TryParse(encoded, template, out var parsed, out var reasonCode), reasonCode);
        Assert.True(CommandActionInputContract.TryMaterialize(encoded, template, out var materialized, out reasonCode), reasonCode);
        Assert.Equal(input.Values.Count, parsed!.Values.Count);
        Assert.Equal("{\"a\":1,\"b\":2}", parsed.Values[2].Value);
        Assert.Equal(["--fixed", "7", "json", "alpha/id", "docs/result.txt", "space ; && $(literal) \"quote\" Ω 😀"], materialized!.Arguments);
        Assert.Equal(new Dictionary<string, string> { ["LANG"] = "C.UTF-8", ["MODE"] = "governed" }, materialized.Environment);
        Assert.Equal("{\"a\":1,\"b\":2}", materialized.StandardInputUtf8);
        Assert.True(CommandActionFingerprint.IsCanonicalSha256(materialized.InputFingerprint));
    }

    [Theory]
    [InlineData(CommandActionSlotKind.Identifier, "alpha/value", true)]
    [InlineData(CommandActionSlotKind.Identifier, "Alpha", false)]
    [InlineData(CommandActionSlotKind.Integer, "7", true)]
    [InlineData(CommandActionSlotKind.Integer, "07", false)]
    [InlineData(CommandActionSlotKind.Integer, "11", false)]
    [InlineData(CommandActionSlotKind.Enumeration, "json", true)]
    [InlineData(CommandActionSlotKind.Enumeration, "yaml", false)]
    [InlineData(CommandActionSlotKind.BoundedText, "a ; b", true)]
    [InlineData(CommandActionSlotKind.BoundedText, "@response", false)]
    [InlineData(CommandActionSlotKind.WorkspaceRelativeTarget, "docs/file.txt", true)]
    [InlineData(CommandActionSlotKind.WorkspaceRelativeTarget, "../escape", false)]
    [InlineData(CommandActionSlotKind.BoundedJson, "{\"b\":2,\"a\":1}", true)]
    [InlineData(CommandActionSlotKind.BoundedJson, "not-json", false)]
    public void Every_slot_kind_is_closed_and_canonical(CommandActionSlotKind kind, string value, bool expected)
    {
        var template = SingleSlotTemplate(kind);
        var input = new CommandActionInput(1, template.TemplateId, template.TemplateVersion, template.ContentHash, [new CommandActionSlotValue("value", kind, value)]);
        string encoded;
        try
        {
            encoded = CommandActionInputContract.Encode(input, template);
        }
        catch (ArgumentException)
        {
            Assert.False(expected);
            return;
        }

        Assert.True(expected);
        Assert.True(CommandActionInputContract.TryMaterialize(encoded, template, out var materialized, out _));
        var expectedValue = kind == CommandActionSlotKind.BoundedJson ? "{\"a\":1,\"b\":2}" : value;
        Assert.Equal(expectedValue, materialized!.Arguments.Single());
    }

    [Fact]
    public void Credential_templates_are_structurally_valid_but_explicitly_marked_unavailable_for_later_host_policy()
    {
        var template = Template(requiresCredentialChannel: true);

        Assert.True(template.RequiresCredentialChannel);
        Assert.Null(CommandActionTemplateContract.Validate(template));
    }

    [Fact]
    public void Template_rejects_unknown_null_duplicate_unordered_and_contradictory_shapes()
    {
        var exact = Template();
        var duplicateSlots = exact.Slots.ToArray();
        duplicateSlots[1] = duplicateSlots[0];
        var unorderedSlots = exact.Slots.Reverse().ToArray();
        var duplicateEnvironment = exact.Environment.ToArray();
        duplicateEnvironment[1] = duplicateEnvironment[0];

        Assert.NotNull(CommandActionTemplateContract.Validate(null));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { SchemaVersion = 2 }));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { Capability = null! }));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { ActivationRevision = 0 }));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { TemplateId = "Not Canonical" }));
        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, slots: duplicateSlots)));
        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, slots: unorderedSlots)));
        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, environment: duplicateEnvironment)));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { StandardInput = CommandActionStandardInputKind.Unknown }));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { SecondaryGrammar = CommandActionSecondaryGrammarPolicy.Unknown }));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { Isolation = exact.Isolation with { RequireProcessTreeTermination = false } }));
        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, arguments: [new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "missing")])));
        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, arguments: [new CommandActionArgumentPart(CommandActionArgumentPartKind.Fixed, "@response")])));
        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, arguments:
        [
            new CommandActionArgumentPart(CommandActionArgumentPartKind.Fixed, "-Command"),
            new CommandActionArgumentPart(CommandActionArgumentPartKind.Fixed, "Write-Output '{\"trusted\":true}'"),
        ])));
        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, arguments:
        [
            new CommandActionArgumentPart(CommandActionArgumentPartKind.Fixed, "--eval"),
            new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "text"),
        ])));
    }

    [Fact]
    public void Template_rejects_every_collection_and_scalar_max_plus_one()
    {
        var exact = Template();
        var slot = exact.Slots[0];

        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, slots: Enumerable.Repeat(slot, CommandActionContractLimits.MaxSlots + 1).ToArray())));
        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, arguments: Enumerable.Repeat(exact.Arguments[0], CommandActionContractLimits.MaxArguments + 1).ToArray())));
        Assert.NotNull(CommandActionTemplateContract.Validate(Copy(exact, environment: Enumerable.Range(0, CommandActionContractLimits.MaxEnvironmentEntries + 1).Select(index => new CommandActionEnvironmentEntry($"X{index}", "v")).ToArray())));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { Isolation = exact.Isolation with { MaxExecutionMilliseconds = CommandActionContractLimits.MaxExecutionMilliseconds + 1 } }));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { Isolation = exact.Isolation with { MaxTerminationMilliseconds = CommandActionContractLimits.MaxTerminationMilliseconds + 1 } }));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { Isolation = exact.Isolation with { MaxMemoryBytes = CommandActionContractLimits.MaxMemoryBytes + 1 } }));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { Isolation = exact.Isolation with { MaxOutputBytes = CommandActionContractLimits.MaxOutputBytes + 1 } }));
        Assert.NotNull(CommandActionTemplateContract.Validate(exact with { Isolation = exact.Isolation with { MaxConcurrency = CommandActionContractLimits.MaxConcurrency + 1 } }));
        Assert.Throws<ArgumentException>(() => CommandActionTemplateContract.Create(2, exact.Capability, exact.Implementation, exact.ArtifactDigest, 1, "template", 1, [], [], [], CommandActionSecondaryGrammarPolicy.None, CommandActionStandardInputKind.Closed, null, CommandActionOutputKind.Json, exact.Isolation, false));
    }

    [Fact]
    public void Input_rejects_forged_template_metadata_unknown_properties_and_noncanonical_values()
    {
        var template = Template();
        var encoded = CommandActionInputContract.Encode(Input(template), template);
        var forgedHash = encoded.Replace(template.ContentHash, new string('0', 64), StringComparison.Ordinal);
        var unknown = encoded[..^1] + ",\"rawCommand\":\"sh -c bad\"}";
        var missing = encoded.Replace(",\"values\":" + encoded.Split("\"values\":", 2)[1][..^1], string.Empty, StringComparison.Ordinal);
        var responseFile = CommandActionInputContract.Encode(Input(template, text: "literal"), template).Replace("\"literal\"", "\"@response\"", StringComparison.Ordinal);

        Assert.False(CommandActionInputContract.TryParse(forgedHash, template, out _, out _));
        Assert.False(CommandActionInputContract.TryParse(unknown, template, out _, out _));
        Assert.False(CommandActionInputContract.TryParse(missing, template, out _, out _));
        Assert.False(CommandActionInputContract.TryParse(responseFile, template, out _, out _));
        Assert.False(CommandActionInputContract.TryParse("not-json", template, out _, out _));
        Assert.False(CommandActionInputContract.TryParse(null, template, out _, out _));
    }

    [Fact]
    public void Fingerprints_and_identifiers_are_strict()
    {
        var hash = CommandActionFingerprint.Compute("test", "a", null, string.Empty);

        Assert.True(CommandActionFingerprint.IsCanonicalSha256(hash));
        Assert.False(CommandActionFingerprint.IsCanonicalSha256(hash.ToUpperInvariant()));
        Assert.True(CommandActionFingerprint.IsEvidenceIdentifier("outcome-abc_1"));
        Assert.False(CommandActionFingerprint.IsEvidenceIdentifier("Bad Evidence"));
        Assert.Throws<ArgumentException>(() => CommandActionFingerprint.Compute("", "a"));
        Assert.Throws<ArgumentNullException>(() => CommandActionFingerprint.Compute("test", null!));
    }

    [Fact]
    public void Graph_descriptor_and_result_are_exact_hash_pinned_value_free_contracts()
    {
        var template = Template();
        var descriptor = CommandActionNodeDescriptors.For(template);
        var result = CommandActionResultContract.Create(
            CommandActionResultStatus.Committed,
            CommandActionResultOutcome.Succeeded,
            "command-outcome-" + new string('c', 64),
            7);
        var encoded = CommandActionResultContract.Encode(result);

        Assert.True(CommandActionNodeDescriptors.IsCommandAction(descriptor));
        Assert.True(CommandActionNodeDescriptors.Matches(descriptor, template));
        Assert.Equal("command-" + template.ContentHash, descriptor.TypeId);
        Assert.True(CommandActionResultContract.TryParse(encoded, out var parsed));
        Assert.Equal(result, parsed);
        Assert.Equal(
            "{\"effectGeneration\":7,\"outcome\":\"succeeded\",\"outcomeEvidenceId\":\"command-outcome-" + new string('c', 64) + "\",\"schemaVersion\":1,\"status\":\"committed\"}",
            encoded);
        Assert.DoesNotContain("stdout", encoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", encoded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Graph_descriptor_and_result_reject_substitution_noncanonical_and_max_plus_one_shapes()
    {
        var template = Template();
        var descriptor = CommandActionNodeDescriptors.For(template);
        var encoded = CommandActionResultContract.Encode(CommandActionResultContract.Create(
            CommandActionResultStatus.Replayed,
            CommandActionResultOutcome.Failed,
            "command-outcome-" + new string('d', 64),
            1));

        Assert.False(CommandActionNodeDescriptors.IsCommandAction(descriptor with { Version = 2 }));
        Assert.False(CommandActionNodeDescriptors.Matches(descriptor with { TypeId = "command-" + new string('e', 64) }, template));
        Assert.False(CommandActionResultContract.TryParse(encoded.Replace("\"replayed\"", "\"unknown\"", StringComparison.Ordinal), out _));
        Assert.False(CommandActionResultContract.TryParse(encoded.Replace("{\"effectGeneration\":1", "{\"schemaVersion\":1,\"effectGeneration\":1", StringComparison.Ordinal), out _));
        Assert.False(CommandActionResultContract.TryParse(encoded + " ", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandActionResultContract.Create(
            CommandActionResultStatus.Committed,
            CommandActionResultOutcome.Succeeded,
            "command-outcome-" + new string('f', 64),
            EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionLimits.MaxVersion + 1));
    }

    [Fact]
    public void Preparation_and_outcome_evidence_are_content_addressed_and_tamper_evident()
    {
        var template = Template();
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var input = new string('1', 64);
        var target = new string('2', 64);
        var precondition = new string('3', 64);
        var before = CommandActionEvidenceContract.CreatePreparation(template, input, target, precondition, now);
        var outcome = CommandActionEvidenceContract.CreateOutcome(
            "effect-alpha", "operation-alpha", 1, template, input, target, precondition, before.EvidenceId,
            CommandActionOutcomeKind.Succeeded, CommandActionTerminationPosture.Exited, 0,
            "{\"message\":\"\\uD83D\\uDE00\",\"ok\":true}", "", 11, 0, 25, now);
        var unicodeOutcome = CommandActionEvidenceContract.CreateOutcome(
            "effect-unicode", "operation-unicode", 1, template, input, target, precondition, before.EvidenceId,
            CommandActionOutcomeKind.NonZeroExit, CommandActionTerminationPosture.Exited, 1,
            "😀", "", 4, 0, 25, now);

        Assert.Null(CommandActionEvidenceContract.ValidatePreparation(before));
        Assert.Null(CommandActionEvidenceContract.ValidateOutcome(outcome));
        Assert.Null(CommandActionEvidenceContract.ValidateOutcome(unicodeOutcome));
        Assert.StartsWith("command-before-", before.EvidenceId, StringComparison.Ordinal);
        Assert.StartsWith("command-outcome-", outcome.EvidenceId, StringComparison.Ordinal);
        Assert.Equal("command-preparation-evidence-id-mismatch", CommandActionEvidenceContract.ValidatePreparation(before with { TargetFingerprint = new string('4', 64) }));
        Assert.Equal("command-outcome-evidence-id-mismatch", CommandActionEvidenceContract.ValidateOutcome(outcome with { RetainedStandardError = "changed" }));
    }

    [Theory]
    [InlineData(CommandActionOutcomeKind.NonZeroExit, CommandActionTerminationPosture.Exited, 7, true)]
    [InlineData(CommandActionOutcomeKind.MalformedResult, CommandActionTerminationPosture.Exited, 0, true)]
    [InlineData(CommandActionOutcomeKind.InvalidEncoding, CommandActionTerminationPosture.Exited, 0, true)]
    [InlineData(CommandActionOutcomeKind.OutputLimitExceeded, CommandActionTerminationPosture.ProcessTreeTerminated, null, true)]
    [InlineData(CommandActionOutcomeKind.TimedOut, CommandActionTerminationPosture.ProcessTreeTerminated, null, true)]
    [InlineData(CommandActionOutcomeKind.Cancelled, CommandActionTerminationPosture.ProcessTreeTerminated, null, true)]
    [InlineData(CommandActionOutcomeKind.IsolationRejected, CommandActionTerminationPosture.NotStarted, null, true)]
    [InlineData(CommandActionOutcomeKind.Unknown, CommandActionTerminationPosture.Unknown, null, false)]
    public void Outcome_state_matrix_is_closed(
        CommandActionOutcomeKind kind,
        CommandActionTerminationPosture termination,
        int? exitCode,
        bool accepted)
    {
        var template = Template();
        var action = () => CommandActionEvidenceContract.CreateOutcome(
            "effect-alpha", "operation-alpha", 1, template, new string('1', 64), new string('2', 64),
            new string('3', 64), "command-before-" + new string('4', 64),
            kind, termination, exitCode, null, null, 0, 0, 25,
            new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));

        if (accepted)
        {
            Assert.Null(CommandActionEvidenceContract.ValidateOutcome(action()));
        }
        else
        {
            Assert.Throws<ArgumentException>(action);
        }
    }

    [Fact]
    public void Evidence_rejects_non_utc_unsafe_unbounded_and_combined_max_plus_one_shapes()
    {
        var template = Template();
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var outcome = CommandActionEvidenceContract.CreateOutcome(
            "effect-alpha", "operation-alpha", 1, template, new string('1', 64), new string('2', 64),
            new string('3', 64), "command-before-" + new string('4', 64),
            CommandActionOutcomeKind.NonZeroExit, CommandActionTerminationPosture.Exited, 1,
            null, null, 0, 0, 25, now);

        Assert.NotNull(CommandActionEvidenceContract.ValidatePreparation(
            CommandActionEvidenceContract.CreatePreparation(template, new string('1', 64), new string('2', 64), new string('3', 64), now) with
            { RecordedAtUtc = now.ToOffset(TimeSpan.FromHours(1)) }));
        Assert.NotNull(CommandActionEvidenceContract.ValidateOutcome(outcome with { RetainedStandardError = "unsafe\0text" }));
        Assert.NotNull(CommandActionEvidenceContract.ValidateOutcome(outcome with
        {
            ObservedStandardOutputBytes = CommandActionContractLimits.MaxOutputBytes,
            ObservedStandardErrorBytes = 2,
        }));
        Assert.NotNull(CommandActionEvidenceContract.ValidateOutcome(outcome with
        {
            RetainedStandardError = new string('x', CommandActionContractLimits.MaxRetainedOutputCharacters + 1),
        }));
    }

    private static CommandActionTemplate Template(
        IReadOnlyList<CommandActionSlotDefinition>? slots = null,
        IReadOnlyList<CommandActionArgumentPart>? arguments = null,
        IReadOnlyList<CommandActionEnvironmentEntry>? environment = null,
        bool requiresCredentialChannel = false)
    {
        var pin = Pin();
        return CommandActionTemplateContract.Create(
            1,
            pin.Capability,
            pin.Implementation,
            pin.Digest,
            3,
            "report/render",
            1,
            slots ?? Slots(),
            arguments ?? Arguments(),
            environment ?? EnvironmentEntries(),
            CommandActionSecondaryGrammarPolicy.None,
            CommandActionStandardInputKind.SlotJson,
            "input",
            CommandActionOutputKind.Json,
            new CommandActionIsolationPolicy(CommandActionWorkingDirectoryKind.ArtifactRoot, CommandActionNetworkPolicy.Denied, 5_000, 2_000, 64_000_000, 16_384, 2, true),
            requiresCredentialChannel);
    }

    private static CommandActionTemplate SingleSlotTemplate(CommandActionSlotKind kind)
    {
        var pin = Pin();
        var definition = kind switch
        {
            CommandActionSlotKind.Integer => new CommandActionSlotDefinition("value", kind, 64, -10, 10, [], true),
            CommandActionSlotKind.Enumeration => new CommandActionSlotDefinition("value", kind, 64, null, null, ["json", "text"], false),
            _ => new CommandActionSlotDefinition("value", kind, 256, null, null, [], false),
        };
        return CommandActionTemplateContract.Create(
            1,
            pin.Capability,
            pin.Implementation,
            pin.Digest,
            1,
            "single/value",
            1,
            [definition],
            [new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "value")],
            [],
            CommandActionSecondaryGrammarPolicy.None,
            CommandActionStandardInputKind.Closed,
            null,
            CommandActionOutputKind.Json,
            new CommandActionIsolationPolicy(CommandActionWorkingDirectoryKind.ArtifactRoot, CommandActionNetworkPolicy.Denied, 1_000, 1_000, 1_000_000, 1_024, 1, true),
            false);
    }

    private static IReadOnlyList<CommandActionSlotDefinition> Slots()
        =>
        [
            new("count", CommandActionSlotKind.Integer, 64, 1, 10, [], false),
            new("format", CommandActionSlotKind.Enumeration, 64, null, null, ["json", "text"], false),
            new("input", CommandActionSlotKind.BoundedJson, 4_096, null, null, [], false),
            new("name", CommandActionSlotKind.Identifier, 128, null, null, [], false),
            new("target", CommandActionSlotKind.WorkspaceRelativeTarget, 512, null, null, [], false),
            new("text", CommandActionSlotKind.BoundedText, 4_096, null, null, [], false),
        ];

    private static IReadOnlyList<CommandActionArgumentPart> Arguments()
        =>
        [
            new(CommandActionArgumentPartKind.Fixed, "--fixed"),
            new(CommandActionArgumentPartKind.Slot, "count"),
            new(CommandActionArgumentPartKind.Slot, "format"),
            new(CommandActionArgumentPartKind.Slot, "name"),
            new(CommandActionArgumentPartKind.Slot, "target"),
            new(CommandActionArgumentPartKind.Slot, "text"),
        ];

    private static IReadOnlyList<CommandActionEnvironmentEntry> EnvironmentEntries()
        => [new("LANG", "C.UTF-8"), new("MODE", "governed")];

    private static CommandActionInput Input(CommandActionTemplate template, string text = "literal")
        => new(
            1,
            template.TemplateId,
            template.TemplateVersion,
            template.ContentHash,
            [
                new("count", CommandActionSlotKind.Integer, "7"),
                new("format", CommandActionSlotKind.Enumeration, "json"),
                new("input", CommandActionSlotKind.BoundedJson, "{\"b\":2,\"a\":1}"),
                new("name", CommandActionSlotKind.Identifier, "alpha/id"),
                new("target", CommandActionSlotKind.WorkspaceRelativeTarget, "docs/result.txt"),
                new("text", CommandActionSlotKind.BoundedText, text),
            ]);

    private static (CapabilityDescriptorIdentity Capability, CapabilityImplementationIdentity Implementation, CapabilityIntegrityDigest Digest) Pin()
    {
        Assert.True(CapabilityId.TryParse("org.example/report", out var id, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var hash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + new string('b', 64), out var digest, out _));
        return (new CapabilityDescriptorIdentity(id!, version!, hash!), new CapabilityImplementationIdentity(provider!, "report/runner"), digest!);
    }

    private static CommandActionTemplate Copy(
        CommandActionTemplate source,
        IReadOnlyList<CommandActionSlotDefinition>? slots = null,
        IReadOnlyList<CommandActionArgumentPart>? arguments = null,
        IReadOnlyList<CommandActionEnvironmentEntry>? environment = null)
        => new(
            source.SchemaVersion,
            source.Capability,
            source.Implementation,
            source.ArtifactDigest,
            source.ActivationRevision,
            source.TemplateId,
            source.TemplateVersion,
            slots ?? source.Slots,
            arguments ?? source.Arguments,
            environment ?? source.Environment,
            source.SecondaryGrammar,
            source.StandardInput,
            source.StandardInputSlot,
            source.Output,
            source.Isolation,
            source.RequiresCredentialChannel,
            source.ContentHash);
}
