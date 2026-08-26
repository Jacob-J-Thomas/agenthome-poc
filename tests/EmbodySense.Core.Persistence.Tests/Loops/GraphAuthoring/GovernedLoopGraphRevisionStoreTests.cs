using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Tests;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring;

public sealed class GovernedLoopGraphRevisionStoreTests
{
    private const string ModelInferenceCapabilityId = GovernedLoopGraphTestFixture.ModelInferenceCapability;
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset _time = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', ContextualRoleLimits.Sha256HexCharacters);

    [Fact]
    public async Task Commit_restart_read_and_exact_replay_preserve_canonical_graph_and_generic_provenance()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph(retryPolicy: RetryPolicy());
        var mutation = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);

        var committed = await Store(paths, trust).CommitAsync(mutation);
        var restarted = Store(paths, trust);
        var graphRead = await restarted.ReadGraphAsync(graph.GraphId);
        var artifactRead = await restarted.ReadArtifactAsync(graph.RevisionReference);
        var mutationRead = await restarted.ReadForMutationAsync(graph.GraphId, "create-one", HashA, HashB);
        var replayed = await restarted.CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, graphRead.Status);
        var expectedArtifact = GovernedLoopGraphRevisionArtifactFactory.Create(
            GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion,
            mutation.LifecycleMutation.ArtifactToAppend!,
            graph);
        var storedArtifact = Assert.Single(graphRead.Snapshot!.Artifacts);
        Assert.Equal(expectedArtifact.ArtifactHash, storedArtifact.ArtifactHash);
        Assert.Equal(graph.RevisionReference, storedArtifact.Graph.RevisionReference);
        Assert.Equal(expectedArtifact.ArtifactHash, artifactRead.Artifact!.ArtifactHash);
        var expectedRetry = Assert.IsType<GovernedLoopRetryPolicy>(graph.Nodes.Single(node => node.Id == "infer").RetryPolicy);
        var restoredRetry = Assert.IsType<GovernedLoopRetryPolicy>(artifactRead.Artifact.Graph.Nodes.Single(node => node.Id == "infer").RetryPolicy);
        Assert.Equal(expectedRetry.ContentHash, restoredRetry.ContentHash);
        Assert.Equal(expectedRetry.FailureClasses, restoredRetry.FailureClasses);
        Assert.Equal(expectedRetry.ServerCodes, restoredRetry.ServerCodes);
        Assert.True(GovernedLoopRetryContract.IsValid(restoredRetry));
        Assert.Equal(GovernedLoopGraphRevisionOperationState.Terminal, mutationRead.ExistingOperation!.State);
        Assert.Equal(HashA, mutationRead.ExistingOperation.LifecycleRequestHash);
        Assert.Equal(HashB, mutationRead.ExistingOperation.AuthoringRequestHash);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(committed.StoreGeneration, replayed.StoreGeneration);

        var payload = await File.ReadAllTextAsync(ArtifactPath(paths, graph));
        Assert.DoesNotContain("createdAtUtc", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("createdByActorId", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("creationOperationId", payload, StringComparison.Ordinal);
        using var payloadJson = JsonDocument.Parse(payload);
        var executableGraph = payloadJson.RootElement.GetProperty("executableGraph");
        Assert.Equal(
            ["schemaVersion", "graphId", "revisionId", "purpose", "owningRole", "entryNodeId"],
            executableGraph.EnumerateObject().Take(6).Select(property => property.Name));
        var owningRole = executableGraph.GetProperty("owningRole");
        Assert.Equal(["contentHash", "revision", "roleId"], owningRole.EnumerateObject().Select(property => property.Name));
        Assert.Equal(graph.OwningRole.ContentHash, owningRole.GetProperty("contentHash").GetString());
        Assert.Equal(graph.OwningRole.Identity.Revision, owningRole.GetProperty("revision").GetInt32());
        Assert.Equal(graph.OwningRole.Identity.RoleId, owningRole.GetProperty("roleId").GetString());
        Assert.DoesNotContain("owningRoleId", payload, StringComparison.Ordinal);
        Assert.Equal("retry-infer", executableGraph.GetProperty("nodes")[1].GetProperty("retryPolicy").GetProperty("policyId").GetString());
    }

    [Fact]
    public async Task Persisted_payload_digest_binds_every_exact_owning_role_pin_component()
    {
        var baseline = Graph();
        var baselineDigest = await PersistedContentDigestAsync(baseline);
        var variants = new[]
        {
            Graph(owningRole: Role("writer", 1, 'a')),
            Graph(owningRole: Role("researcher", 2, 'a')),
            Graph(owningRole: Role("researcher", 1, 'b')),
        };

        foreach (var variant in variants)
        {
            Assert.NotEqual(baselineDigest, await PersistedContentDigestAsync(variant));
        }
    }

    [Fact]
    public async Task File_trust_provider_supports_create_restart_and_exact_replay_with_canonical_digests()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trustRoot = Path.Combine(workspace.ServerStatePath, "graph-revision-trust");
        var graph = Graph();
        var mutation = CreateDraft(graph, "create-file-trust", HashA, HashB, 0, _time);

        var firstTrust = new FileCapabilityCatalogTrustProvider(trustRoot);
        var firstAuthority = new CapabilityAuthorityTransaction(paths);
        var firstLifecycle = new GovernedLoopRevisionLifecycleStore(
            paths,
            firstTrust,
            authorityTransaction: firstAuthority);
        var committed = await new GovernedLoopGraphRevisionStore(
            paths,
            firstLifecycle,
            firstTrust,
            authorityTransaction: firstAuthority).CommitAsync(mutation);

        var restartedTrust = new FileCapabilityCatalogTrustProvider(trustRoot);
        var restartedAuthority = new CapabilityAuthorityTransaction(paths);
        var restartedLifecycle = new GovernedLoopRevisionLifecycleStore(
            paths,
            restartedTrust,
            authorityTransaction: restartedAuthority);
        var restarted = new GovernedLoopGraphRevisionStore(
            paths,
            restartedLifecycle,
            restartedTrust,
            authorityTransaction: restartedAuthority);
        var replayed = await restarted.CommitAsync(mutation);
        var read = await restarted.ReadArtifactAsync(graph.RevisionReference);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, read.Status);
        using var artifactJson = JsonDocument.Parse(await File.ReadAllBytesAsync(ArtifactPath(paths, graph)));
        Assert.Matches("^sha256:[0-9a-f]{64}$", artifactJson.RootElement.GetProperty("contentDigest").GetString());
        using var intentJson = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(GraphRoot(paths), "operations", "create-file-trust.json")));
        Assert.Matches("^sha256:[0-9a-f]{64}$", intentJson.RootElement.GetProperty("contentDigest").GetString());
    }

    [Fact]
    public async Task Layout_only_successor_keeps_executable_identity_but_changes_layout_and_full_artifact_hash()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var firstGraph = Graph();
        var first = CreateDraft(firstGraph, "create-one", HashA, HashB, 0, _time);
        var store = Store(paths, trust);
        var created = await store.CommitAsync(first);
        var secondGraph = Graph(
            revisionId: "revision-two",
            display: Display("Moved graph", 400, 500));
        var second = ReplaceDraft(first, secondGraph, "replace-one", HashB, HashC, 1, _time.AddMinutes(1));

        var replaced = await store.CommitAsync(second);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, created.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, replaced.Status);
        Assert.Equal(firstGraph.ExecutableHash, secondGraph.ExecutableHash);
        var artifacts = replaced.Snapshot!.Artifacts;
        Assert.Equal(2, artifacts.Count);
        Assert.NotEqual(artifacts[0].LayoutHash, artifacts[1].LayoutHash);
        Assert.NotEqual(artifacts[0].ArtifactHash, artifacts[1].ArtifactHash);
        Assert.Equal(firstGraph.RevisionReference, artifacts[1].RevisionArtifact.PredecessorRevision);
    }

    [Fact]
    public async Task Every_schema_one_graph_enum_round_trips_through_canonical_persistence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = GraphWithEveryClosedEnum();
        var mutation = CreateDraft(graph, "create-all-enums", HashA, HashB, 0, _time);

        var committed = await Store(paths, trust).CommitAsync(mutation);
        var read = await Store(paths, trust).ReadArtifactAsync(graph.RevisionReference);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, read.Status);
        Assert.Equal(
            Enum.GetValues<GovernedLoopNodeKind>().Where(value => value != GovernedLoopNodeKind.Unknown).Order(),
            read.Artifact!.Graph.Nodes.Select(node => node.Descriptor.Kind).Order());
        Assert.Equal(
            Enum.GetValues<GovernedLoopValueKind>().Where(value => value != GovernedLoopValueKind.Unknown).Order(),
            read.Artifact.Graph.ValueSchemas.Select(schema => schema.Kind).Order());
        Assert.Equal(
            Enum.GetValues<GovernedLoopControlCondition>().Where(value => value != GovernedLoopControlCondition.Unknown).Order(),
            read.Artifact.Graph.ControlEdges.Select(edge => edge.Condition).Order());
        Assert.Contains(read.Artifact.Graph.Bindings, binding => binding.Kind == GovernedLoopBindingKind.Context);
        var humanInput = Assert.Single(read.Artifact.Graph.Nodes, node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanInput);
        Assert.Equal(GovernedLoopHumanInputVocabulary.TypeId, humanInput.Descriptor.TypeId);
        Assert.Equal("text", humanInput.HumanInputConfiguration!.RequestSchemaReference);
        Assert.Equal("timeout-policy-one", humanInput.HumanInputConfiguration.TimeoutPolicyReference);
        Assert.Equal("failure-policy-one", humanInput.HumanInputConfiguration.FailurePolicyReference);
    }

    [Fact]
    public async Task Human_input_configuration_is_restart_stable_defensively_copied_and_rejects_unknown_nested_fields()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = GraphWithEveryClosedEnum();
        var mutation = CreateDraft(graph, "create-human-input", HashA, HashB, 0, _time);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await Store(paths, trust).CommitAsync(mutation)).Status);
        var restarted = await Store(paths, trust).ReadArtifactAsync(graph.RevisionReference);
        var restoredConfiguration = Assert.Single(restarted.Artifact!.Graph.Nodes, node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanInput).HumanInputConfiguration!;
        var originalConfiguration = Assert.Single(graph.Nodes, node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanInput).HumanInputConfiguration!;

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, restarted.Status);
        Assert.NotSame(originalConfiguration, restoredConfiguration);
        Assert.NotSame(originalConfiguration.ResponseSchema, restoredConfiguration.ResponseSchema);
        Assert.NotSame(originalConfiguration.EligibleRespondents, restoredConfiguration.EligibleRespondents);
        Assert.Equal(originalConfiguration.SchemaVersion, restoredConfiguration.SchemaVersion);
        Assert.Equal(originalConfiguration.RequestSchemaReference, restoredConfiguration.RequestSchemaReference);
        Assert.Equal(originalConfiguration.Purpose, restoredConfiguration.Purpose);
        Assert.Equal(originalConfiguration.Prompt, restoredConfiguration.Prompt);
        Assert.Equal(originalConfiguration.PrivacyClass, restoredConfiguration.PrivacyClass);
        Assert.Equal(originalConfiguration.TimeoutPolicyReference, restoredConfiguration.TimeoutPolicyReference);
        Assert.Equal(originalConfiguration.FailurePolicyReference, restoredConfiguration.FailurePolicyReference);
        Assert.Equal(originalConfiguration.ResponseSchema, restoredConfiguration.ResponseSchema);
        Assert.Equal(originalConfiguration.EligibleRespondents, restoredConfiguration.EligibleRespondents);
        Assert.Equal(originalConfiguration.ResponsePolicy, restoredConfiguration.ResponsePolicy);
        var path = ArtifactPath(paths, graph);
        var bytes = await File.ReadAllBytesAsync(path);
        var corrupted = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(
            "\"humanInputConfiguration\": {",
            "\"humanInputConfiguration\": {\"unknown\":true,",
            StringComparison.Ordinal));
        Assert.False(bytes.SequenceEqual(corrupted));
        await File.WriteAllBytesAsync(path, corrupted);

        var rejected = await Store(paths, trust).ReadArtifactAsync(graph.RevisionReference);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, rejected.Status);
        Assert.Null(rejected.Artifact);
    }

    [Fact]
    public async Task Every_supported_human_input_schema_and_policy_variant_round_trips_through_canonical_persistence()
    {
        var variants = new[]
        {
            new
            {
                Name = "text-first-valid",
                Configuration = HumanInputConfiguration(
                    "text",
                    HumanInputPrivacyClass.Private,
                    new HumanInputResponseSchema(HumanInputResponseKind.Text, 64, null, null, null),
                    new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null)),
            },
            new
            {
                Name = "choice-quorum",
                Configuration = HumanInputConfiguration(
                    "text",
                    HumanInputPrivacyClass.Private,
                    new HumanInputResponseSchema(HumanInputResponseKind.Choice, null, [new HumanInputChoice("no", "No"), new HumanInputChoice("yes", "Yes")], null, null),
                    new HumanInputResponsePolicy(HumanInputResponsePolicyKind.Quorum, 2, null)),
            },
            new
            {
                Name = "confirmation-named-roles",
                Configuration = HumanInputConfiguration(
                    "boolean",
                    HumanInputPrivacyClass.Sensitive,
                    new HumanInputResponseSchema(HumanInputResponseKind.Confirmation, null, null, null, null),
                    new HumanInputResponsePolicy(HumanInputResponsePolicyKind.NamedRoles, null, ["role-one", "role-two"])),
            },
            new
            {
                Name = "structured-merge",
                Configuration = HumanInputConfiguration(
                    "object",
                    HumanInputPrivacyClass.Private,
                    new HumanInputResponseSchema(
                        HumanInputResponseKind.Structured,
                        null,
                        null,
                        [
                            new HumanInputStructuredFieldSchema("text-field", HumanInputStructuredFieldKind.Text, true, 64, null),
                            new HumanInputStructuredFieldSchema("choice-field", HumanInputStructuredFieldKind.Choice, false, null, [new HumanInputChoice("one", "One"), new HumanInputChoice("two", "Two")]),
                        ],
                        null),
                    new HumanInputResponsePolicy(HumanInputResponsePolicyKind.Merge, 2, ["role-one", "role-two"])),
            },
            new
            {
                Name = "reference-artifact-manual-selection",
                Configuration = HumanInputConfiguration(
                    "text",
                    HumanInputPrivacyClass.Private,
                    new HumanInputResponseSchema(HumanInputResponseKind.Reference, null, null, null, new HumanInputReferencePolicy(HumanInputReferenceKind.Artifact, 128)),
                    new HumanInputResponsePolicy(HumanInputResponsePolicyKind.ManualSelection, null, ["role-one"])),
            },
            new
            {
                Name = "reference-inline-first-valid",
                Configuration = HumanInputConfiguration(
                    "text",
                    HumanInputPrivacyClass.Sensitive,
                    new HumanInputResponseSchema(HumanInputResponseKind.Reference, null, null, null, new HumanInputReferencePolicy(HumanInputReferenceKind.Reference, 128)),
                    new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null)),
            },
        };

        foreach (var variant in variants)
        {
            using var workspace = new TestWorkspace();
            var paths = new WorkspacePaths(workspace.RootPath);
            var trust = new TestCapabilityLifecycleTrustProvider();
            Assert.True(GovernedLoopHumanInputNodeConfigurationValidator.IsValid(variant.Configuration), variant.Name);
            var graph = GraphWithEveryClosedEnum(variant.Configuration, requireBooleanNonNullable: true);
            var mutation = CreateDraft(graph, "create-" + variant.Name, HashA, HashB, 0, _time);

            var committed = await Store(paths, trust).CommitAsync(mutation);
            var restarted = await Store(paths, trust).ReadArtifactAsync(graph.RevisionReference);
            var restored = Assert.Single(restarted.Artifact!.Graph.Nodes, node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanInput).HumanInputConfiguration!;

            Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
            Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, restarted.Status);
            AssertEquivalentHumanInputConfiguration(variant.Configuration, restored);
            using var payload = JsonDocument.Parse(await File.ReadAllBytesAsync(ArtifactPath(paths, graph)));
            var persisted = Assert.Single(payload.RootElement.GetProperty("executableGraph").GetProperty("nodes").EnumerateArray(), node => node.GetProperty("kind").GetString() == "human-input").GetProperty("humanInputConfiguration");
            Assert.Equal(variant.Configuration.ResponseSchema!.Kind.ToString().ToLowerInvariant(), persisted.GetProperty("responseSchema").GetProperty("kind").GetString());
            Assert.Equal(variant.Configuration.PrivacyClass.ToString().ToLowerInvariant(), persisted.GetProperty("privacyClass").GetString());
            Assert.Equal(variant.Configuration.ResponsePolicy!.Kind.ToString().ToLowerInvariant().Replace("manualselection", "manual-selection", StringComparison.Ordinal).Replace("firstvalid", "first-valid", StringComparison.Ordinal).Replace("namedroles", "named-roles", StringComparison.Ordinal), persisted.GetProperty("responsePolicy").GetProperty("kind").GetString());
        }
    }

    private static void AssertEquivalentHumanInputConfiguration(
        GovernedLoopHumanInputNodeConfiguration expected,
        GovernedLoopHumanInputNodeConfiguration actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.RequestSchemaReference, actual.RequestSchemaReference);
        Assert.Equal(expected.Purpose, actual.Purpose);
        Assert.Equal(expected.Prompt, actual.Prompt);
        Assert.Equal(expected.PrivacyClass, actual.PrivacyClass);
        Assert.Equal(expected.TimeoutPolicyReference, actual.TimeoutPolicyReference);
        Assert.Equal(expected.FailurePolicyReference, actual.FailurePolicyReference);
        Assert.Equal(expected.EligibleRespondents!.Select(respondent => (respondent!.RespondentId, respondent.RespondentRoleId, respondent.RoutingReference)), actual.EligibleRespondents!.Select(respondent => (respondent!.RespondentId, respondent.RespondentRoleId, respondent.RoutingReference)));
        Assert.Equal(expected.ResponsePolicy!.Kind, actual.ResponsePolicy!.Kind);
        Assert.Equal(expected.ResponsePolicy.RequiredResponseCount, actual.ResponsePolicy.RequiredResponseCount);
        Assert.Equal(expected.ResponsePolicy.OrderedRoleIds?.ToArray(), actual.ResponsePolicy.OrderedRoleIds?.ToArray());
        Assert.Equal(expected.ResponseSchema!.Kind, actual.ResponseSchema!.Kind);
        Assert.Equal(expected.ResponseSchema.MaxTextCharacters, actual.ResponseSchema.MaxTextCharacters);
        Assert.Equal(expected.ResponseSchema.Choices?.Length, actual.ResponseSchema.Choices?.Length);
        if (expected.ResponseSchema.Choices is not null)
        {
            Assert.Equal(expected.ResponseSchema.Choices.Select(choice => (choice!.ChoiceId, choice.DisplayText)), actual.ResponseSchema.Choices!.Select(choice => (choice!.ChoiceId, choice.DisplayText)));
        }
        Assert.Equal(expected.ResponseSchema.StructuredFields?.Length, actual.ResponseSchema.StructuredFields?.Length);
        if (expected.ResponseSchema.StructuredFields is not null)
        {
            Assert.Equal(expected.ResponseSchema.StructuredFields.Length, actual.ResponseSchema.StructuredFields!.Length);
            for (var index = 0; index < expected.ResponseSchema.StructuredFields.Length; index++)
            {
                var expectedField = expected.ResponseSchema.StructuredFields[index]!;
                var actualField = actual.ResponseSchema.StructuredFields[index]!;
                Assert.Equal(expectedField.FieldId, actualField.FieldId);
                Assert.Equal(expectedField.Kind, actualField.Kind);
                Assert.Equal(expectedField.Required, actualField.Required);
                Assert.Equal(expectedField.MaxTextCharacters, actualField.MaxTextCharacters);
                Assert.Equal(expectedField.Choices?.Length, actualField.Choices?.Length);
                if (expectedField.Choices is not null)
                {
                    Assert.Equal(expectedField.Choices.Select(choice => (choice!.ChoiceId, choice.DisplayText)), actualField.Choices!.Select(choice => (choice!.ChoiceId, choice.DisplayText)));
                }
            }
        }
        Assert.Equal(expected.ResponseSchema.ReferencePolicy?.Kind, actual.ResponseSchema.ReferencePolicy?.Kind);
        Assert.Equal(expected.ResponseSchema.ReferencePolicy?.MaxReferenceCharacters, actual.ResponseSchema.ReferencePolicy?.MaxReferenceCharacters);
    }

    [Fact]
    public async Task Captured_nested_human_input_values_cannot_diverge_from_persisted_graph_identity_or_json()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var nestedChoices = new[] { new HumanInputChoice("nested-choice", "Nested choice"), new HumanInputChoice("other-choice", "Other choice") };
        var structuredFields = new[] { new HumanInputStructuredFieldSchema("field-one", HumanInputStructuredFieldKind.Choice, true, null, nestedChoices) };
        var configuration = new GovernedLoopHumanInputNodeConfiguration(
            GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion,
            "object",
            "Collect untrusted structured data.",
            "Choose one bounded nested value.",
            new HumanInputResponseSchema(HumanInputResponseKind.Structured, null, null, structuredFields, null),
            HumanInputPrivacyClass.Private,
            [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")],
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            "timeout-policy-one",
            "failure-policy-one");
        var graph = GraphWithEveryClosedEnum(configuration);
        var hash = graph.ExecutableHash;
        var captured = Assert.Single(graph.Nodes, node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanInput).HumanInputConfiguration!;

        nestedChoices[0] = new HumanInputChoice("source-mutated", "Source mutated");
        structuredFields[0] = new HumanInputStructuredFieldSchema("source-mutated", HumanInputStructuredFieldKind.Text, false, 64, null);
        captured.ResponseSchema!.StructuredFields![0].Choices![0] = new HumanInputChoice("returned-mutated", "Returned mutated");
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await Store(paths, trust).CommitAsync(CreateDraft(graph, "create-nested-human-input", HashA, HashB, 0, _time))).Status);
        using var payload = JsonDocument.Parse(await File.ReadAllBytesAsync(ArtifactPath(paths, graph)));
        var persistedConfiguration = Assert.Single(
            payload.RootElement.GetProperty("executableGraph").GetProperty("nodes").EnumerateArray(),
            node => node.GetProperty("kind").GetString() == "human-input").GetProperty("humanInputConfiguration");
        var restarted = await Store(paths, trust).ReadArtifactAsync(graph.RevisionReference);

        Assert.Equal(hash, graph.ExecutableHash);
        Assert.Equal("field-one", persistedConfiguration.GetProperty("responseSchema").GetProperty("structuredFields")[0].GetProperty("fieldId").GetString());
        Assert.Equal("nested-choice", persistedConfiguration.GetProperty("responseSchema").GetProperty("structuredFields")[0].GetProperty("choices")[0].GetProperty("choiceId").GetString());
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, restarted.Status);
        Assert.Equal(hash, restarted.Artifact!.Graph.ExecutableHash);
    }

    [Fact]
    public async Task Legacy_wait_and_human_review_artifacts_restart_with_pinned_hashes_and_omit_human_input_configuration()
    {
        var legacyNodes = new (string Name, GovernedLoopNodeDefinition Node, string ExpectedHash)[]
        {
            (
                "wait",
                new GovernedLoopNodeDefinition(
                    "infer",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, "wait-timestamp", 1),
                    [InputPort("request"), OutputPort("result")],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string> { ["deadline-utc"] = "2026-08-13T01:02:03.4567890Z" }),
                "b02aea19be748a3b8f1a9b9ccaee120588551968aed75401fb083d365c98a54a"),
            (
                "human-review",
                new GovernedLoopNodeDefinition(
                    "infer",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanReview, "human-review", 1),
                    [InputPort("request"), OutputPort("result")],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
                "cd0080d3f9aeca9480585a304a18a0d9aff21dd66da8198c0577fe05e030649e"),
        };

        foreach (var legacy in legacyNodes)
        {
            using var workspace = new TestWorkspace();
            var paths = new WorkspacePaths(workspace.RootPath);
            var trust = new TestCapabilityLifecycleTrustProvider();
            var graph = Graph(
                graphId: "legacy-" + legacy.Name,
                revisionId: "legacy-" + legacy.Name + "-revision",
                intermediate: legacy.Node);
            var mutation = CreateDraft(graph, "create-legacy-" + legacy.Name, HashA, HashB, 0, _time);

            Assert.Equal(legacy.ExpectedHash, graph.ExecutableHash);
            Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await Store(paths, trust).CommitAsync(mutation)).Status);
            var payload = await File.ReadAllTextAsync(ArtifactPath(paths, graph));
            using var payloadJson = JsonDocument.Parse(payload);
            var restarted = await Store(paths, trust).ReadArtifactAsync(graph.RevisionReference);

            Assert.All(payloadJson.RootElement.GetProperty("executableGraph").GetProperty("nodes").EnumerateArray(), node => Assert.False(node.TryGetProperty("humanInputConfiguration", out _)));
            Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, restarted.Status);
            Assert.Equal(graph.ExecutableHash, restarted.Artifact!.Graph.ExecutableHash);
            Assert.DoesNotContain(restarted.Artifact.Graph.Nodes, node => node.HumanInputConfiguration is not null);
        }
    }

    [Fact]
    public async Task Lifecycle_only_publication_persists_full_intent_without_a_second_graph_payload()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var draft = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);
        var store = Store(paths, trust);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(draft)).Status);
        var publish = Publish(draft, "publish-one", HashB, HashC, 1, _time.AddMinutes(1));

        var published = await store.CommitAsync(publish);
        var replayed = await Store(paths, trust).CommitAsync(publish);
        var read = await Store(paths, trust).ReadForMutationAsync(graph.GraphId, "publish-one", HashB, HashC);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, published.Status);
        Assert.Equal(GovernedLoopRevisionLifecycleStatus.Published, published.Snapshot!.Lifecycle.Head.Status);
        Assert.Single(published.Snapshot.Artifacts);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Replayed, replayed.Status);
        Assert.Equal(GovernedLoopGraphRevisionOperationState.Terminal, read.ExistingOperation!.State);
        Assert.Equal(HashC, read.ExistingOperation.GraphValidationEvidenceHash);
        var intent = await File.ReadAllTextAsync(
            Path.Combine(GraphRoot(paths), "operations", "publish-one.json"));
        Assert.Contains("\"graphPayloadHash\": null", intent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payload_only_crash_is_recoverable_when_lifecycle_timestamp_is_replanned()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var original = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);
        var replanned = CreateDraft(graph, "create-one", HashA, HashB, 0, _time.AddHours(1));

        var interrupted = await Store(paths, trust, FailAt(GovernedLoopGraphRevisionPersistenceBoundary.ArtifactPublished))
            .CommitAsync(original);
        var recovered = await Store(paths, trust).CommitAsync(replanned);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
        Assert.Equal(_time.AddHours(1), Assert.Single(recovered.Snapshot!.Artifacts).RevisionArtifact.CreatedAtUtc);
    }

    [Fact]
    public async Task Published_intent_reports_pending_only_for_exact_dual_hashes_and_exact_retry_finishes()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var mutation = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);

        var interrupted = await Store(paths, trust, FailAt(GovernedLoopGraphRevisionPersistenceBoundary.IntentPublished))
            .CommitAsync(mutation);
        var restarted = Store(paths, trust);
        var exact = await restarted.ReadForMutationAsync(graph.GraphId, "create-one", HashA, HashB);
        var changedAuthoring = await restarted.ReadForMutationAsync(graph.GraphId, "create-one", HashA, HashC);
        var recovered = await restarted.CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopGraphRevisionOperationState.Pending, exact.ExistingOperation!.State);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.NotFound, changedAuthoring.Status);
        Assert.Equal(HashB, changedAuthoring.ExistingOperation!.AuthoringRequestHash);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
    }

    [Fact]
    public async Task Payload_only_orphan_remains_recoverable_after_another_operation_advances_graph_trust()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var firstGraph = Graph(graphId: "graph-one", revisionId: "revision-one");
        var secondGraph = Graph(graphId: "graph-two", revisionId: "revision-two");
        var first = CreateDraft(firstGraph, "create-one", HashA, HashB, 0, _time);
        var second = CreateDraft(secondGraph, "create-two", HashB, HashC, 0, _time.AddMinutes(1));

        var interrupted = await Store(paths, trust, FailAt(GovernedLoopGraphRevisionPersistenceBoundary.ArtifactPublished))
            .CommitAsync(first);
        var advanced = await Store(paths, trust).CommitAsync(second);
        var refreshed = CreateDraft(firstGraph, "create-one", HashA, HashB, 1, _time.AddMinutes(2));
        var recovered = await Store(paths, trust).CommitAsync(refreshed);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, advanced.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
        Assert.Equal(2, recovered.StoreGeneration);
        Assert.Equal(_time.AddMinutes(2), Assert.Single(recovered.Snapshot!.Artifacts).RevisionArtifact.CreatedAtUtc);
    }

    [Fact]
    public async Task Pending_intent_is_reconciled_before_a_later_operation_allocates_the_next_trust_generation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var firstGraph = Graph(graphId: "graph-one", revisionId: "revision-one");
        var secondGraph = Graph(graphId: "graph-two", revisionId: "revision-two");
        var first = CreateDraft(firstGraph, "create-one", HashA, HashB, 0, _time);
        var second = CreateDraft(secondGraph, "create-two", HashB, HashC, 0, _time.AddMinutes(1));

        var interrupted = await Store(paths, trust, FailAt(GovernedLoopGraphRevisionPersistenceBoundary.IntentPublished))
            .CommitAsync(first);
        var later = await Store(paths, trust).CommitAsync(second);

        var firstIntent = await IntentTrustGenerationAsync(paths, "create-one");
        var secondIntent = await IntentTrustGenerationAsync(paths, "create-two");
        var refreshed = CreateDraft(firstGraph, "create-one", HashA, HashB, 1, _time);
        var recovered = await Store(paths, trust).CommitAsync(refreshed);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, later.Status);
        Assert.Equal(1, firstIntent);
        Assert.Equal(2, secondIntent);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
        Assert.Equal(2, recovered.StoreGeneration);
    }

    [Fact]
    public async Task Read_paths_do_not_create_storage_and_first_commit_creates_only_the_exact_root()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var graph = Graph();
        var root = GraphRoot(paths);

        var graphRead = await store.ReadGraphAsync(graph.GraphId);
        var mutationRead = await store.ReadForMutationAsync(graph.GraphId, "create-one", HashA, HashB);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.NotFound, graphRead.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.NotFound, mutationRead.Status);
        Assert.False(Directory.Exists(root));

        var committed = await store.CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time));

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(root, ".mutations.lock")));
    }

    [Fact]
    public async Task Root_inspection_uses_its_held_lock_handle_but_still_rejects_an_unsafe_nonlock_entry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var graph = Graph();

        var committed = await store.CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time));

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        var operationsPath = Path.Combine(GraphRoot(paths), "operations");
        Directory.Move(operationsPath, workspace.File("retained-operations"));
        var external = workspace.File("unsafe-root-entry.json");
        await File.WriteAllTextAsync(external, "unsafe");
        CreateHardLink(operationsPath, external);

        var read = await store.ReadGraphAsync(graph.GraphId);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, read.Status);
        Assert.Null(read.Snapshot);
    }

    [Fact]
    public async Task Visible_lifecycle_without_its_exact_payload_fails_closed_without_fallback_scanning()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var store = Store(paths, trust);
        Assert.Equal(
            GovernedLoopRevisionStoreCommitStatus.Committed,
            (await store.CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time))).Status);
        var exactPath = ArtifactPath(paths, graph);
        var decoyPath = Path.Combine(Path.GetDirectoryName(exactPath)!, "decoy-revision.json");
        File.Move(exactPath, decoyPath);

        var graphRead = await store.ReadGraphAsync(graph.GraphId);
        var artifactRead = await store.ReadArtifactAsync(graph.RevisionReference);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, graphRead.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, artifactRead.Status);
        Assert.Null(graphRead.Snapshot);
        Assert.Null(artifactRead.Artifact);
    }

    [Fact]
    public async Task Malformed_unreferenced_artifacts_and_missing_current_intents_fail_the_workspace_closed()
    {
        using var malformedWorkspace = new TestWorkspace();
        var malformedPaths = new WorkspacePaths(malformedWorkspace.RootPath);
        var malformedTrust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var malformedStore = Store(malformedPaths, malformedTrust);
        Assert.Equal(
            GovernedLoopRevisionStoreCommitStatus.Committed,
            (await malformedStore.CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time))).Status);
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(ArtifactPath(malformedPaths, graph))!, "orphan-revision.json"),
            "{}\n");

        Assert.Equal(
            GovernedLoopRevisionStoreReadStatus.Ambiguous,
            (await malformedStore.ReadGraphAsync(graph.GraphId)).Status);

        using var missingIntentWorkspace = new TestWorkspace();
        var missingIntentPaths = new WorkspacePaths(missingIntentWorkspace.RootPath);
        var missingIntentTrust = new TestCapabilityLifecycleTrustProvider();
        var missingIntentStore = Store(missingIntentPaths, missingIntentTrust);
        Assert.Equal(
            GovernedLoopRevisionStoreCommitStatus.Committed,
            (await missingIntentStore.CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time))).Status);
        File.Delete(Path.Combine(GraphRoot(missingIntentPaths), "operations", "create-one.json"));

        Assert.Equal(
            GovernedLoopRevisionStoreReadStatus.Ambiguous,
            (await missingIntentStore.ReadGraphAsync(graph.GraphId)).Status);
    }

    [Fact]
    public async Task Restarted_reads_fail_closed_when_historical_intent_is_missing_but_current_trust_remains_intact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trustRoot = Path.Combine(workspace.ServerStatePath, "graph-revision-trust");
        var firstGraph = Graph();
        var first = CreateDraft(firstGraph, "create-one", HashA, HashB, 0, _time);
        var secondGraph = Graph(revisionId: "revision-two");
        var second = ReplaceDraft(first, secondGraph, "replace-one", HashB, HashC, 1, _time.AddMinutes(1));
        var firstTrust = new FileCapabilityCatalogTrustProvider(trustRoot);
        var firstAuthority = new CapabilityAuthorityTransaction(paths);
        var firstLifecycle = new GovernedLoopRevisionLifecycleStore(
            paths,
            firstTrust,
            authorityTransaction: firstAuthority);
        var firstStore = new GovernedLoopGraphRevisionStore(
            paths,
            firstLifecycle,
            firstTrust,
            authorityTransaction: firstAuthority);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await firstStore.CommitAsync(first)).Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await firstStore.CommitAsync(second)).Status);
        Assert.Equal(2, await IntentTrustGenerationAsync(paths, "replace-one"));
        var currentIntentPath = Path.Combine(GraphRoot(paths), "operations", "replace-one.json");
        File.Delete(Path.Combine(GraphRoot(paths), "operations", "create-one.json"));
        Assert.True(File.Exists(currentIntentPath));

        var restartedTrust = new FileCapabilityCatalogTrustProvider(trustRoot);
        var restartedAuthority = new CapabilityAuthorityTransaction(paths);
        var restartedLifecycle = new GovernedLoopRevisionLifecycleStore(
            paths,
            restartedTrust,
            authorityTransaction: restartedAuthority);
        var restarted = new GovernedLoopGraphRevisionStore(
            paths,
            restartedLifecycle,
            restartedTrust,
            authorityTransaction: restartedAuthority);

        var graphRead = await restarted.ReadGraphAsync(firstGraph.GraphId);
        var artifactRead = await restarted.ReadArtifactAsync(secondGraph.RevisionReference);
        var mutationRead = await restarted.ReadForMutationAsync(
            firstGraph.GraphId,
            "replace-one",
            HashB,
            HashC);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, graphRead.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, artifactRead.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, mutationRead.Status);
        Assert.Null(graphRead.Snapshot);
        Assert.Null(artifactRead.Artifact);
        Assert.Null(mutationRead.Snapshot);
        Assert.Null(mutationRead.ExistingOperation);
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("noncanonical")]
    [InlineData("raw-content-digest")]
    [InlineData("legacy-owning-role")]
    [InlineData("mixed-owning-role")]
    [InlineData("malformed-owning-role")]
    public async Task Malformed_or_noncanonical_payloads_are_never_projected(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var store = Store(paths, trust);
        Assert.Equal(
            GovernedLoopRevisionStoreCommitStatus.Committed,
            (await store.CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time))).Status);
        var path = ArtifactPath(paths, graph);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var text = Encoding.UTF8.GetString(originalBytes);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var bytes = corruption switch
        {
            "bom" => [0xef, 0xbb, 0xbf, .. originalBytes],
            "duplicate" => Encoding.UTF8.GetBytes(text.Replace("{" + newline, "{" + newline + "  \"schemaVersion\": 1," + newline, StringComparison.Ordinal)),
            "unknown" => Encoding.UTF8.GetBytes(text.Replace("{" + newline, "{" + newline + "  \"unknown\": true," + newline, StringComparison.Ordinal)),
            "noncanonical" => Encoding.UTF8.GetBytes(newline == "\n" ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text.Replace("\r\n", "\n", StringComparison.Ordinal)),
            "raw-content-digest" => Encoding.UTF8.GetBytes(text.Replace("\"contentDigest\": \"sha256:", "\"contentDigest\": \"", StringComparison.Ordinal)),
            "legacy-owning-role" => Encoding.UTF8.GetBytes(ReplaceOwningRole(text, newline, "\"owningRoleId\": \"researcher\"")),
            "mixed-owning-role" => Encoding.UTF8.GetBytes(text.Replace("\"owningRole\": {", "\"owningRoleId\": \"researcher\"," + newline + "    \"owningRole\": {", StringComparison.Ordinal)),
            "malformed-owning-role" => Encoding.UTF8.GetBytes(text.Replace("\"revision\": 1", "\"revision\": 0", StringComparison.Ordinal)),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        Assert.False(originalBytes.SequenceEqual(bytes));
        await File.WriteAllBytesAsync(path, bytes);

        var read = await store.ReadArtifactAsync(graph.RevisionReference);

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ambiguous, read.Status);
        Assert.Null(read.Artifact);
    }

    [Fact]
    public async Task Canonical_partial_writing_is_bounded_and_inert_while_unknown_staging_fails_closed()
    {
        using var toleratedWorkspace = new TestWorkspace();
        var toleratedPaths = new WorkspacePaths(toleratedWorkspace.RootPath);
        var toleratedOperations = Path.Combine(GraphRoot(toleratedPaths), "operations");
        Directory.CreateDirectory(toleratedOperations);
        await File.WriteAllTextAsync(
            Path.Combine(toleratedOperations, ".orphan.json.0123456789abcdef0123456789abcdef.writing"),
            "partial");
        var graph = Graph();
        var tolerated = await Store(toleratedPaths, new TestCapabilityLifecycleTrustProvider())
            .CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time));

        using var rejectedWorkspace = new TestWorkspace();
        var rejectedPaths = new WorkspacePaths(rejectedWorkspace.RootPath);
        var rejectedOperations = Path.Combine(GraphRoot(rejectedPaths), "operations");
        Directory.CreateDirectory(rejectedOperations);
        await File.WriteAllTextAsync(Path.Combine(rejectedOperations, "orphan.tmp"), "partial");
        var rejected = await Store(rejectedPaths, new TestCapabilityLifecycleTrustProvider())
            .CommitAsync(CreateDraft(graph, "create-two", HashA, HashB, 0, _time));

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, tolerated.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Unavailable, rejected.Status);
    }

    [Fact]
    public async Task Aggregate_and_staging_capacity_are_enforced_before_immutable_publication()
    {
        using var bytesWorkspace = new TestWorkspace();
        var bytesPaths = new WorkspacePaths(bytesWorkspace.RootPath);
        var graph = Graph();
        var bytesResult = await Store(
                bytesPaths,
                new TestCapabilityLifecycleTrustProvider(),
                new GovernedLoopGraphRevisionStoreOptions
                {
                    MaxArtifactUtf8Bytes = 64,
                    MaxIntentUtf8Bytes = 32 * 1024,
                    MaxWorkspaceUtf8Bytes = 8 * 1024,
                })
            .CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time));

        using var stagingWorkspace = new TestWorkspace();
        var stagingPaths = new WorkspacePaths(stagingWorkspace.RootPath);
        var operations = Path.Combine(GraphRoot(stagingPaths), "operations");
        Directory.CreateDirectory(operations);
        await File.WriteAllTextAsync(
            Path.Combine(operations, ".orphan.json.0123456789abcdef0123456789abcdef.writing"),
            "partial");
        var stagingResult = await Store(
                stagingPaths,
                new TestCapabilityLifecycleTrustProvider(),
                new GovernedLoopGraphRevisionStoreOptions { MaxStagingEntries = 1 })
            .CommitAsync(CreateDraft(graph, "create-two", HashA, HashB, 0, _time));

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, bytesResult.Status);
        Assert.False(File.Exists(ArtifactPath(bytesPaths, graph)));
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, stagingResult.Status);
        Assert.False(File.Exists(Path.Combine(operations, "create-two.json")));
    }

    [Fact]
    public async Task Exact_ready_stage_recovers_at_one_entry_and_exact_workspace_byte_limits()
    {
        using var measurementWorkspace = new TestWorkspace();
        var measurementPaths = new WorkspacePaths(measurementWorkspace.RootPath);
        var graph = Graph();
        var mutation = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);
        Assert.Equal(
            GovernedLoopRevisionStoreCommitStatus.Committed,
            (await Store(measurementPaths, new TestCapabilityLifecycleTrustProvider()).CommitAsync(mutation)).Status);
        var intentLength = new FileInfo(Path.Combine(GraphRoot(measurementPaths), "operations", "create-one.json")).Length;

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var interrupted = await Store(paths, trust, FailAt(GovernedLoopGraphRevisionPersistenceBoundary.ArtifactPublished))
            .CommitAsync(mutation);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        var artifactPath = ArtifactPath(paths, graph);
        var artifactBytes = await File.ReadAllBytesAsync(artifactPath);
        var readyPath = ImmutableReadyPath(artifactPath, artifactBytes);
        File.Move(artifactPath, readyPath);

        var recovered = await Store(
                paths,
                trust,
                new GovernedLoopGraphRevisionStoreOptions
                {
                    MaxStagingEntries = 1,
                    MaxWorkspaceUtf8Bytes = artifactBytes.LongLength + intentLength,
                })
            .CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
        Assert.True(File.Exists(artifactPath));
        Assert.False(File.Exists(readyPath));
        Assert.Equal(
            artifactBytes.LongLength + intentLength,
            Directory.EnumerateFiles(GraphRoot(paths), "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".mutations.lock", StringComparison.Ordinal))
                .Sum(path => new FileInfo(path).Length));
    }

    [Fact]
    public async Task Exact_artifact_destination_is_reflushed_after_failed_final_rename_before_lifecycle_visibility()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var mutation = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);
        var barrier = new FailingDestinationDurabilityBarrier(ArtifactPath(paths, graph));
        var options = new GovernedLoopGraphRevisionStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) =>
            {
                if (boundary == GovernedLoopGraphRevisionPersistenceBoundary.IntentPublished)
                {
                    Assert.True(barrier.TargetFlushCount >= 2);
                }
                return ValueTask.CompletedTask;
            },
        };

        var interrupted = await Store(paths, trust, options, barrier).CommitAsync(mutation);
        var recovered = await Store(paths, trust, options, barrier).CommitAsync(mutation);
        var visible = await Store(paths, trust).ReadGraphAsync(graph.GraphId);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
        Assert.True(barrier.TargetFlushCount >= 2);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, visible.Status);
    }

    [Fact]
    public async Task Exact_intent_destination_is_reflushed_after_failed_final_rename_before_trust_and_lifecycle()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var mutation = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);
        var intentPath = Path.Combine(GraphRoot(paths), "operations", "create-one.json");
        var barrier = new FailingDestinationDurabilityBarrier(intentPath);
        var options = new GovernedLoopGraphRevisionStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) =>
            {
                if (boundary == GovernedLoopGraphRevisionPersistenceBoundary.TrustAdvanced)
                {
                    Assert.True(barrier.TargetFlushCount >= 2);
                }
                return ValueTask.CompletedTask;
            },
        };

        var interrupted = await Store(paths, trust, options, barrier).CommitAsync(mutation);
        var recovered = await Store(paths, trust, options, barrier).CommitAsync(mutation);
        var visible = await Store(paths, trust).ReadGraphAsync(graph.GraphId);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
        Assert.True(barrier.TargetFlushCount >= 2);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, visible.Status);
    }

    [Fact]
    public async Task Pending_intent_reflush_failure_remains_ambiguous_and_later_recovery_commits()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var mutation = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);
        var intentPath = Path.Combine(GraphRoot(paths), "operations", "create-one.json");
        var barrier = new FailingDestinationDurabilityBarrier(intentPath, failureCount: 2);

        var interrupted = await Store(paths, trust, durabilityBarrier: barrier).CommitAsync(mutation);
        var reconciliationInterrupted = await Store(paths, trust, durabilityBarrier: barrier).CommitAsync(mutation);
        var reconciliationFlushCount = barrier.TargetFlushCount;
        var pending = await Store(paths, trust).ReadForMutationAsync(graph.GraphId, "create-one", HashA, HashB);
        var recovered = await Store(paths, trust, durabilityBarrier: barrier).CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, reconciliationInterrupted.Status);
        Assert.Equal(2, reconciliationFlushCount);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.NotFound, pending.Status);
        Assert.Equal(GovernedLoopGraphRevisionOperationState.Pending, pending.ExistingOperation!.State);
        Assert.Equal(HashA, pending.ExistingOperation.LifecycleRequestHash);
        Assert.Equal(HashB, pending.ExistingOperation.AuthoringRequestHash);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
        Assert.True(barrier.TargetFlushCount >= 4);
        Assert.Equal(
            GovernedLoopRevisionStoreReadStatus.Ready,
            (await Store(paths, trust).ReadGraphAsync(graph.GraphId)).Status);
    }

    [Fact]
    public async Task Trust_initialization_failure_after_possible_publication_is_ambiguous_and_recoverable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider
        {
            AfterInitialize = _ => throw new IOException("Injected post-initialization availability failure."),
        };
        var graph = Graph();
        var mutation = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);

        var interrupted = await Store(paths, trust).CommitAsync(mutation);
        trust.AfterInitialize = null;
        var recovered = await Store(paths, trust).CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.Null(interrupted.Operation);
        Assert.Null(interrupted.Snapshot);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
        Assert.Equal(
            GovernedLoopRevisionStoreReadStatus.Ready,
            (await Store(paths, trust).ReadGraphAsync(graph.GraphId)).Status);
    }

    [Fact]
    public async Task Exact_race_winner_destination_is_reflushed_before_intent_trust_and_lifecycle()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var mutation = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);
        var artifactPath = ArtifactPath(paths, graph);
        var barrier = new CompetingExactDestinationDurabilityBarrier(artifactPath);

        var interrupted = await Store(paths, trust, durabilityBarrier: barrier).CommitAsync(mutation);

        Assert.True(barrier.InjectedCompetingDestination);
        Assert.Equal(1, barrier.TargetFlushCount);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, interrupted.Status);
        Assert.True(File.Exists(artifactPath));
        Assert.False(File.Exists(Path.Combine(GraphRoot(paths), "operations", "create-one.json")));

        var recovered = await Store(paths, trust, durabilityBarrier: barrier).CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, recovered.Status);
        Assert.Equal(2, barrier.TargetFlushCount);
        Assert.Equal(
            GovernedLoopRevisionStoreReadStatus.Ready,
            (await Store(paths, trust).ReadGraphAsync(graph.GraphId)).Status);
    }

    [Fact]
    public async Task Malformed_nested_lifecycle_mutations_are_rejected_before_graph_storage_exists()
    {
        var graph = Graph();
        var valid = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);
        var lifecycle = valid.LifecycleMutation;
        var malformed = new[]
        {
            valid with { LifecycleMutation = lifecycle with { ExpectedStoreGeneration = -1 } },
            valid with { LifecycleMutation = lifecycle with { Operation = lifecycle.Operation with { ActorId = "BAD ID" } } },
            valid with
            {
                LifecycleMutation = lifecycle with
                {
                    HeadToWrite = lifecycle.HeadToWrite! with { LifecycleVersion = 2 },
                },
            },
            valid with
            {
                LifecycleMutation = lifecycle with
                {
                    ArtifactToAppend = lifecycle.ArtifactToAppend! with { CreatedByActorId = "actor-two" },
                },
            },
            valid with { GraphValidationEvidenceHash = null },
        };

        foreach (var mutation in malformed)
        {
            using var workspace = new TestWorkspace();
            var paths = new WorkspacePaths(workspace.RootPath);

            var result = await Store(paths, new TestCapabilityLifecycleTrustProvider()).CommitAsync(mutation);

            Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Unavailable, result.Status);
            Assert.False(Directory.Exists(GraphRoot(paths)));
        }
    }

    [Fact]
    public async Task Committed_validation_evidence_matrix_rejects_mismatch_without_reserving_operations()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var created = CreateDraft(graph, "create-one", HashA, HashB, 0, _time);
        var store = Store(paths, trust);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(created)).Status);

        var publish = Publish(created, "publish-one", HashB, HashC, 1, _time.AddMinutes(1));
        var publishMismatch = publish with { GraphValidationEvidenceHash = HashB };
        var publishResult = await store.CommitAsync(publishMismatch);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Unavailable, publishResult.Status);
        Assert.False(File.Exists(Path.Combine(GraphRoot(paths), "operations", "publish-one.json")));

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(publish)).Status);
        var disable = Disable(publish, "disable-one", HashA, HashB, 2, _time.AddMinutes(2));
        var disableResult = await store.CommitAsync(disable with { GraphValidationEvidenceHash = HashB });

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Unavailable, disableResult.Status);
        Assert.False(File.Exists(Path.Combine(GraphRoot(paths), "operations", "disable-one.json")));

        using var rollbackWorkspace = new TestWorkspace();
        var rollbackPaths = new WorkspacePaths(rollbackWorkspace.RootPath);
        var rollback = Rollback(Graph(revisionId: "revision-three"), "rollback-one", HashA, HashB, 0, _time);
        Assert.True(GovernedLoopRevisionStoreMutationGuard.IsValid(rollback.LifecycleMutation));
        var rollbackResult = await Store(rollbackPaths, new TestCapabilityLifecycleTrustProvider())
            .CommitAsync(rollback with { GraphValidationEvidenceHash = HashB });

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Unavailable, rollbackResult.Status);
        Assert.False(Directory.Exists(GraphRoot(rollbackPaths)));
    }

    [Fact]
    public async Task Noncommitted_missing_replace_may_retain_valid_graph_validation_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var graph = Graph();
        var committedShape = CreateDraft(graph, "replace-one", HashA, HashB, 0, _time);
        var failureOperation = committedShape.LifecycleMutation.Operation with
        {
            Kind = GovernedLoopRevisionOperationKind.ReplaceDraft,
            Outcome = GovernedLoopRevisionOperationOutcome.NotFound,
            FailureCode = GovernedLoopRevisionOperationFailureCode.LifecycleNotFound,
            ResultHead = null,
            TargetRevision = Graph(revisionId: "missing-revision").RevisionReference,
        };
        var failure = new GovernedLoopGraphRevisionStoreMutation(
            new GovernedLoopRevisionStoreMutation(graph.GraphId, 0, failureOperation, null, null),
            null,
            HashB,
            HashC);
        var operationValidation = GovernedLoopRevisionContractValidator.Validate(failureOperation);
        Assert.True(operationValidation.IsValid, string.Join(" | ", operationValidation.Errors.Select(error => $"{error.Path}: {error.Message}")));
        Assert.True(GovernedLoopRevisionStoreMutationGuard.IsValid(failure.LifecycleMutation));

        var result = await Store(paths, new TestCapabilityLifecycleTrustProvider()).CommitAsync(failure);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, result.Status);
        Assert.Equal(GovernedLoopRevisionOperationOutcome.NotFound, result.Operation!.LifecycleOperation!.Evidence.Outcome);
        Assert.Equal(HashC, result.Operation.GraphValidationEvidenceHash);
        Assert.True(File.Exists(Path.Combine(GraphRoot(paths), "operations", "replace-one.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshotless_terminal_commit_with_malformed_or_missing_lifecycle_evidence_fails_closed(
        bool missingEvidence)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var mutation = MissingReplace(Graph(), "replace-one", HashA, HashB, 0, _time);
        var malformed = mutation.LifecycleMutation.Operation with
        {
            Kind = (GovernedLoopRevisionOperationKind)int.MaxValue,
        };
        var hostileEvidence = missingEvidence ? null! : malformed;
        Assert.False(GovernedLoopRevisionContractValidator.Validate(hostileEvidence).IsValid);
        var lifecycle = new InjectedGovernedLoopRevisionLifecycleStore(
            mutation => SnapshotlessCommit(mutation, hostileEvidence, GovernedLoopRevisionStoreCommitStatus.Committed));
        var store = new GovernedLoopGraphRevisionStore(paths, lifecycle, trust);

        var result = await store.CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, result.Status);
        Assert.Null(result.Operation);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData(GovernedLoopRevisionStoreCommitStatus.Committed)]
    [InlineData(GovernedLoopRevisionStoreCommitStatus.Replayed)]
    public async Task Snapshotless_terminal_commit_with_different_valid_lifecycle_evidence_fails_closed(
        GovernedLoopRevisionStoreCommitStatus status)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var mutation = MissingReplace(graph, "replace-one", HashA, HashB, 0, _time);
        var different = mutation.LifecycleMutation.Operation with
        {
            ActorId = "actor-two",
            Kind = GovernedLoopRevisionOperationKind.CreateDraft,
            CandidateRevision = Graph(revisionId: "other-candidate").RevisionReference,
            TargetRevision = null,
        };
        Assert.True(GovernedLoopRevisionContractValidator.Validate(different).IsValid);
        Assert.NotEqual(mutation.LifecycleMutation.Operation, different);
        var lifecycle = new InjectedGovernedLoopRevisionLifecycleStore(
            mutation => SnapshotlessCommit(mutation, different, status));
        var store = new GovernedLoopGraphRevisionStore(paths, lifecycle, trust);

        var result = await store.CommitAsync(mutation);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, result.Status);
        Assert.Null(result.Operation);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task Mutation_read_returns_authenticated_bound_operation_for_changed_request_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var graph = Graph();
        var mutation = CreateDraft(graph, "shared-operation", HashA, HashB, 0, _time);
        var store = Store(paths, trust);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, (await store.CommitAsync(mutation)).Status);

        var changedAuthoring = await store.ReadForMutationAsync(graph.GraphId, "shared-operation", HashA, HashC);
        var changedLifecycle = await store.ReadForMutationAsync(graph.GraphId, "shared-operation", HashC, HashB);
        var changedGraph = await store.ReadForMutationAsync("other-graph", "shared-operation", HashA, HashB);

        foreach (var read in new[] { changedAuthoring, changedLifecycle, changedGraph })
        {
            Assert.NotEqual(GovernedLoopRevisionStoreReadStatus.Ambiguous, read.Status);
            Assert.Equal(GovernedLoopGraphRevisionOperationState.Terminal, read.ExistingOperation!.State);
            Assert.Equal(graph.GraphId, read.ExistingOperation.GraphId);
            Assert.Equal(HashA, read.ExistingOperation.LifecycleRequestHash);
            Assert.Equal(HashB, read.ExistingOperation.AuthoringRequestHash);
        }
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.NotFound, changedGraph.Status);
    }

    [Fact]
    public async Task Post_publication_hard_link_substitution_is_removed_without_poisoning_the_destination()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var graph = Graph();
        var destination = ArtifactPath(paths, graph);
        var external = Path.Combine(workspace.RootPath, "external-substitute.json");
        var retained = Path.Combine(workspace.RootPath, "retained-valid-artifact.json");
        await File.WriteAllTextAsync(external, "attacker-controlled");
        var observer = new HardLinkingPublicationObserver(destination, external, retained);

        var result = await Store(
                paths,
                new TestCapabilityLifecycleTrustProvider(),
                new GovernedLoopGraphRevisionStoreOptions { PathObserver = observer })
            .CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time));

        Assert.True(observer.Substituted);
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Ambiguous, result.Status);
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(retained));
        Assert.Equal("attacker-controlled", await File.ReadAllTextAsync(external));
    }

    [Fact]
    public async Task Real_authoring_service_classifies_changed_reused_operation_as_conflict()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var authorityTransaction = new CapabilityAuthorityTransaction(paths);
        var lifecycleStore = new GovernedLoopRevisionLifecycleStore(
            paths,
            trust,
            authorityTransaction: authorityTransaction);
        var graphStore = new GovernedLoopGraphRevisionStore(
            paths,
            lifecycleStore,
            trust,
            authorityTransaction: authorityTransaction);
        var graph = Graph();
        var candidate = Candidate(graph);
        var service = AuthoringService(graphStore, graph, authorityTransaction);
        var lifecycle = CreateRequest("shared-operation", graph);

        var committed = await service.MutateAsync(new GovernedLoopGraphAuthoringRequest(1, lifecycle, candidate));
        var changedGraph = Graph(display: Display("Changed layout", 700, 800));
        var conflict = await service.MutateAsync(new GovernedLoopGraphAuthoringRequest(
            1,
            lifecycle,
            Candidate(changedGraph)));
        var persisted = await graphStore.ReadGraphAsync(graph.GraphId);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, committed.Status);
        Assert.Equal(GovernedLoopGraphAuthoringStatus.Conflict, conflict.Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Ready, persisted.Status);
        var persistedDisplay = Assert.Single(persisted.Snapshot!.Artifacts).Graph.DisplayMetadata;
        Assert.Equal(graph.DisplayMetadata.DisplayName, persistedDisplay.DisplayName);
        Assert.Equal(graph.DisplayMetadata.Nodes[0].CanvasX, persistedDisplay.Nodes[0].CanvasX);
    }

    [Fact]
    public async Task Concurrent_instances_serialize_the_global_generation_and_operation_reuse_conflicts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var firstGraph = Graph(graphId: "graph-one", revisionId: "revision-one");
        var secondGraph = Graph(graphId: "graph-two", revisionId: "revision-two");
        var commits = await Task.WhenAll(
            Store(paths, trust).CommitAsync(CreateDraft(firstGraph, "create-one", HashA, HashB, 0, _time)),
            Store(paths, trust).CommitAsync(CreateDraft(secondGraph, "create-two", HashB, HashC, 0, _time)));

        Assert.Single(commits, result => result.Status == GovernedLoopRevisionStoreCommitStatus.Committed);
        Assert.Single(commits, result => result.Status == GovernedLoopRevisionStoreCommitStatus.StoreConflict);

        var winner = commits.Single(result => result.Status == GovernedLoopRevisionStoreCommitStatus.Committed);
        var winnerGraph = winner.Snapshot!.Artifacts.Single().Graph;
        var changedAuthoring = CreateDraft(winnerGraph, winner.Operation!.OperationId, winner.Operation.LifecycleRequestHash, HashA, 0, _time);
        var reused = await Store(paths, trust).CommitAsync(changedAuthoring);

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.OperationConflict, reused.Status);
    }

    [Fact]
    public async Task Default_system_graph_is_rejected_without_creating_graph_authoring_storage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var graph = Graph(graphId: BuiltInLoopIds.DefaultConversation);

        var result = await Store(paths, new TestCapabilityLifecycleTrustProvider())
            .CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time));

        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Unavailable, result.Status);
        Assert.False(Directory.Exists(GraphRoot(paths)));
    }

    [Fact]
    public async Task Public_boundary_rejects_invalid_reads_reports_missing_exact_revisions_and_propagates_pre_cancellation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var store = Store(paths, trust);
        var graph = Graph();

        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Unavailable, (await store.ReadGraphAsync("invalid graph")).Status);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.Unavailable, (await store.ReadArtifactAsync(null!)).Status);
        Assert.Equal(
            GovernedLoopRevisionStoreReadStatus.Unavailable,
            (await store.ReadForMutationAsync(graph.GraphId, "invalid operation", HashA, HashB)).Status);

        Assert.Equal(
            GovernedLoopRevisionStoreCommitStatus.Committed,
            (await store.CommitAsync(CreateDraft(graph, "create-one", HashA, HashB, 0, _time))).Status);
        var missing = GovernedLoopRevisionReference.Create(1, graph.GraphId, "missing-revision", graph.ExecutableHash);
        Assert.Equal(GovernedLoopRevisionStoreReadStatus.NotFound, (await store.ReadArtifactAsync(missing)).Status);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadGraphAsync(graph.GraphId, canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.CommitAsync(
            CreateDraft(Graph(graphId: "graph-two"), "create-two", HashB, HashC, 1, _time),
            canceled.Token));
    }

    [Fact]
    public void Constructor_enforces_bounded_configuration_and_supports_default_server_trust_composition()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new TestCapabilityLifecycleTrustProvider();
        var authority = new CapabilityAuthorityTransaction(paths);
        var lifecycle = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: authority);

        _ = new GovernedLoopGraphRevisionStore(paths, lifecycle);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopGraphRevisionStore(
            paths,
            lifecycle,
            trust,
            new GovernedLoopGraphRevisionStoreOptions { MaxArtifacts = 0 },
            authorityTransaction: authority));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopGraphRevisionStore(
            paths,
            lifecycle,
            new TestCapabilityLifecycleTrustProvider(0),
            authorityTransaction: authority));
    }

    private static GovernedLoopGraphRevisionStore Store(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trust,
        GovernedLoopGraphRevisionStoreOptions? graphOptions = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null)
    {
        var authority = new CapabilityAuthorityTransaction(paths);
        var lifecycle = new GovernedLoopRevisionLifecycleStore(
            paths,
            trust,
            authorityTransaction: authority);
        return new GovernedLoopGraphRevisionStore(
            paths,
            lifecycle,
            trust,
            graphOptions,
            durabilityBarrier,
            authorityTransaction: authority);
    }

    private static GovernedLoopGraphRevisionStoreOptions FailAt(
        GovernedLoopGraphRevisionPersistenceBoundary boundary)
        => new()
        {
            DurableBoundaryObserver = (observed, _) => observed == boundary
                ? ValueTask.FromException(new IOException("Injected durable-boundary interruption."))
                : ValueTask.CompletedTask,
        };

    private static GovernedLoopRevisionStoreCommitResult SnapshotlessCommit(
        GovernedLoopRevisionStoreMutation mutation,
        GovernedLoopRevisionOperationEvidence evidence,
        GovernedLoopRevisionStoreCommitStatus status)
        => new(
            status,
            mutation.ExpectedStoreGeneration + 1,
            new GovernedLoopRevisionStoredOperation(mutation.GraphId, evidence),
            null);

    private static GovernedLoopGraphRevisionStoreMutation MissingReplace(
        GovernedLoopGraphDefinition graph,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        long generation,
        DateTimeOffset time)
    {
        var committedShape = CreateDraft(
            graph,
            operationId,
            lifecycleRequestHash,
            authoringRequestHash,
            generation,
            time);
        var operation = committedShape.LifecycleMutation.Operation with
        {
            Kind = GovernedLoopRevisionOperationKind.ReplaceDraft,
            Outcome = GovernedLoopRevisionOperationOutcome.NotFound,
            FailureCode = GovernedLoopRevisionOperationFailureCode.LifecycleNotFound,
            ResultHead = null,
            TargetRevision = Graph(graph.GraphId, "missing-revision").RevisionReference,
        };
        return new GovernedLoopGraphRevisionStoreMutation(
            new GovernedLoopRevisionStoreMutation(graph.GraphId, generation, operation, null, null),
            null,
            authoringRequestHash,
            HashC);
    }

    private static GovernedLoopGraphRevisionStoreMutation CreateDraft(
        GovernedLoopGraphDefinition graph,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        long generation,
        DateTimeOffset time)
    {
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            graph.GraphId,
            1,
            GovernedLoopRevisionLifecycleStatus.Draft,
            graph.RevisionReference,
            null,
            operationId,
            time);
        var artifact = GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            null,
            null,
            operationId,
            "actor-one",
            time);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-one",
            lifecycleRequestHash,
            GovernedLoopRevisionOperationKind.CreateDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            null,
            head,
            graph.RevisionReference,
            null,
            null,
            HashA,
            null,
            time);
        return new GovernedLoopGraphRevisionStoreMutation(
            new GovernedLoopRevisionStoreMutation(graph.GraphId, generation, operation, artifact, head),
            graph,
            authoringRequestHash,
            HashC);
    }

    private static GovernedLoopGraphRevisionStoreMutation ReplaceDraft(
        GovernedLoopGraphRevisionStoreMutation previous,
        GovernedLoopGraphDefinition graph,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        long generation,
        DateTimeOffset time)
    {
        var previousHead = previous.LifecycleMutation.HeadToWrite!;
        var predecessor = previousHead.DraftRevision!;
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            graph.GraphId,
            previousHead.LifecycleVersion + 1,
            GovernedLoopRevisionLifecycleStatus.Draft,
            graph.RevisionReference,
            null,
            operationId,
            time);
        var artifact = GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            predecessor,
            null,
            operationId,
            "actor-one",
            time);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-one",
            lifecycleRequestHash,
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previousHead,
            head,
            graph.RevisionReference,
            predecessor,
            null,
            HashA,
            null,
            time);
        return new GovernedLoopGraphRevisionStoreMutation(
            new GovernedLoopRevisionStoreMutation(graph.GraphId, generation, operation, artifact, head),
            graph,
            authoringRequestHash,
            HashC);
    }

    private static GovernedLoopGraphRevisionStoreMutation Publish(
        GovernedLoopGraphRevisionStoreMutation previous,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        long generation,
        DateTimeOffset time)
    {
        var previousHead = previous.LifecycleMutation.HeadToWrite!;
        var target = previousHead.DraftRevision!;
        var pin = GovernedLoopRevisionPublicationPinFactory.Create(1, target, operationId, HashC);
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            previousHead.GraphId,
            previousHead.LifecycleVersion + 1,
            GovernedLoopRevisionLifecycleStatus.Published,
            null,
            pin,
            operationId,
            time);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-one",
            lifecycleRequestHash,
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previousHead,
            head,
            null,
            target,
            null,
            HashA,
            HashC,
            time);
        return new GovernedLoopGraphRevisionStoreMutation(
            new GovernedLoopRevisionStoreMutation(previousHead.GraphId, generation, operation, null, head),
            null,
            authoringRequestHash,
            HashC);
    }

    private static GovernedLoopGraphRevisionStoreMutation Disable(
        GovernedLoopGraphRevisionStoreMutation previous,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        long generation,
        DateTimeOffset time)
    {
        var previousHead = previous.LifecycleMutation.HeadToWrite!;
        var target = previousHead.PublishedRevision!.Revision;
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            previousHead.GraphId,
            previousHead.LifecycleVersion + 1,
            GovernedLoopRevisionLifecycleStatus.Disabled,
            previousHead.DraftRevision,
            previousHead.PublishedRevision,
            operationId,
            time);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-one",
            lifecycleRequestHash,
            GovernedLoopRevisionOperationKind.Disable,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previousHead,
            head,
            null,
            target,
            null,
            HashA,
            null,
            time);
        return new GovernedLoopGraphRevisionStoreMutation(
            new GovernedLoopRevisionStoreMutation(previousHead.GraphId, generation, operation, null, head),
            null,
            authoringRequestHash,
            null);
    }

    private static GovernedLoopGraphRevisionStoreMutation Rollback(
        GovernedLoopGraphDefinition graph,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        long generation,
        DateTimeOffset time)
    {
        var source = Graph(revisionId: "revision-one").RevisionReference;
        var current = Graph(revisionId: "revision-two").RevisionReference;
        var sourcePin = GovernedLoopRevisionPublicationPinFactory.Create(1, source, "publish-source", HashA);
        var currentPin = GovernedLoopRevisionPublicationPinFactory.Create(1, current, "publish-current", HashB);
        var previousHead = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            graph.GraphId,
            4,
            GovernedLoopRevisionLifecycleStatus.Published,
            null,
            currentPin,
            "publish-current",
            time.AddMinutes(-1));
        var resultPin = GovernedLoopRevisionPublicationPinFactory.Create(1, graph.RevisionReference, operationId, HashC);
        var head = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            graph.GraphId,
            5,
            GovernedLoopRevisionLifecycleStatus.Published,
            null,
            resultPin,
            operationId,
            time);
        var artifact = GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            current,
            sourcePin,
            operationId,
            "actor-one",
            time);
        var operation = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            operationId,
            "actor-one",
            lifecycleRequestHash,
            GovernedLoopRevisionOperationKind.Rollback,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            previousHead,
            head,
            graph.RevisionReference,
            current,
            sourcePin,
            HashA,
            HashC,
            time);
        return new GovernedLoopGraphRevisionStoreMutation(
            new GovernedLoopRevisionStoreMutation(graph.GraphId, generation, operation, artifact, head),
            graph,
            authoringRequestHash,
            HashC);
    }

    private static GovernedLoopGraphAuthoringService AuthoringService(
        GovernedLoopGraphRevisionStore store,
        GovernedLoopGraphDefinition graph,
        ICapabilityAuthorityTransaction authorityTransaction)
    {
        var schemas = graph.ValueSchemas.ToDictionary(schema => schema.Id, schema => schema.Kind, StringComparer.Ordinal);
        var terminals = graph.TerminalNodeIds.ToHashSet(StringComparer.Ordinal);
        var descriptors = graph.Nodes.Select(node =>
        {
            var outcomes = graph.ControlEdges
                .Where(edge => edge.FromNodeId == node.Id)
                .Select(edge => edge.Condition)
                .Distinct()
                .Order()
                .ToArray();
            return new GovernedLoopNodeCatalogDescriptor(
                node.Descriptor,
                true,
                true,
                string.Equals(node.Id, graph.EntryNodeId, StringComparison.Ordinal),
                terminals.Contains(node.Id),
                outcomes,
                outcomes,
                GovernedLoopJoinPolicy.None,
                0,
                false,
                null,
                null,
                node.Ports.Select(port => new GovernedLoopCatalogPortContract(
                    port.Id,
                    port.Direction,
                    port.BindingKind,
                    GovernedLoopValueKindSet.Create([schemas[port.ValueSchemaId]]),
                    port.Required)).ToArray(),
                node.Parameters.Select(parameter => new GovernedLoopCatalogParameterContract(
                    parameter.Key,
                    GovernedLoopParameterValueKind.Text,
                    true,
                    1,
                    CustomLoopLimits.MaxGraphParameterValueCharacters,
                    null,
                    null,
                    [])).ToArray(),
                node.AuthorityCeiling.CapabilityIds,
                new GovernedLoopNodeResourceBudget(0, 0, 0, 0));
        }).ToArray();
        var role = ValidRoleRevision();
        var validation = new GovernedLoopGraphValidationService(
            new FixedNodeCatalog(new GovernedLoopNodeCatalogSnapshot(true, "catalog-one", descriptors)),
            new FixedAuthorityProvider(new GovernedLoopAuthoritySnapshot(
                true,
                HashC,
                graph.OwningRole,
                role,
                new ContextualRoleLifecycleSnapshot(
                    1,
                    role.Identity.RoleId,
                    role.Identity,
                    ContextualRoleLifecycleState.Active,
                    "publish-role",
                    ContextualRoleRevisionMutationKind.Create,
                    _time.AddMinutes(-10)),
                _workspaceId,
                ContextualRoleInstructionSourceProbeStatus.Ready,
                role.PolicyMaxima.CapabilityIds,
                CustomLoopLimits.MaxGraphNodeAttempts,
                100_000,
                CustomLoopLimits.MaxGraphNodeEvidenceItems,
                100)));
        return new GovernedLoopGraphAuthoringService(
            store,
            validation,
            new AuthorizedActor(),
            authorityTransaction,
            new FixedTimeProvider(_time));
    }

    private static GovernedLoopGraphCandidate Candidate(GovernedLoopGraphDefinition graph)
        => new(
            graph.SchemaVersion,
            graph.GraphId,
            graph.RevisionId,
            graph.Purpose,
            graph.OwningRole,
            graph.EntryNodeId,
            graph.TerminalNodeIds,
            graph.AuthorityCeiling,
            graph.ValueSchemas,
            graph.Nodes,
            graph.ControlEdges,
            graph.Bindings,
            graph.OutputContract,
            graph.DisplayMetadata,
            graph.DefaultModelRoutingPolicy);

    private static GovernedLoopRevisionLifecycleRequest CreateRequest(
        string operationId,
        GovernedLoopGraphDefinition graph)
        => new(
            1,
            operationId,
            GovernedLoopRevisionOperationKind.CreateDraft,
            graph.GraphId,
            Actor(),
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            graph.RevisionReference,
            null,
            null);

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("actor-one", out var actor, out _));
        return actor!;
    }

    private static GovernedLoopGraphDefinition Graph(
        string graphId = "graph-one",
        string revisionId = "revision-one",
        GovernedLoopDisplayMetadata? display = null,
        ContextualRoleRevisionPin? owningRole = null,
        GovernedLoopRetryPolicy? retryPolicy = null,
        GovernedLoopNodeDefinition? intermediate = null)
    {
        var canonicalIntermediate = intermediate ?? new GovernedLoopNodeDefinition(
            "infer",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1),
            [InputPort("request"), OutputPort("result")],
            GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId]),
            new Dictionary<string, string> { ["instruction"] = "Answer from the explicit input." },
            null,
            null,
            retryPolicy);
        return GovernedLoopGraphDefinition.Create(
            1,
            graphId,
            revisionId,
            "Answer one bounded request.",
            owningRole ?? ValidRolePin(),
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            [
                new GovernedLoopNodeDefinition(
                    "trigger",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
                    [OutputPort("request")],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
                canonicalIntermediate,
                new GovernedLoopNodeDefinition(
                    "exit",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                    [InputPort("result"), OutputPort("published-result")],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("request-binding", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new GovernedLoopBindingDefinition("result-binding", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            new GovernedLoopOutputContract(
                "Return the answer.",
                [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            display ?? Display("Graph one", 100, 200),
            GovernedLoopGraphTestFixture.DefaultModelRoutingPolicy());
    }

    private static GovernedLoopRetryPolicy RetryPolicy()
        => GovernedLoopRetryContract.CreatePolicy(
            "retry-infer",
            "infer",
            [GovernedLoopFailureClass.RetryableNoEffect],
            [],
            3,
            1_000,
            10_000,
            GovernedLoopRetryBackoffStrategy.Fixed,
            250,
            250,
            GovernedLoopRetryJitterStrategy.None,
            0,
            maximumTokens: 3_000);

    private static GovernedLoopGraphDefinition GraphWithEveryClosedEnum(
        GovernedLoopHumanInputNodeConfiguration? humanInputConfiguration = null,
        bool requireBooleanNonNullable = false)
    {
        var configuration = humanInputConfiguration ?? new GovernedLoopHumanInputNodeConfiguration(
            GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion,
            "text",
            "Collect untrusted data.",
            "Provide a bounded response.",
            new HumanInputResponseSchema(HumanInputResponseKind.Text, 64, null, null, null),
            HumanInputPrivacyClass.Private,
            [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")],
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            "timeout-policy-one",
            "failure-policy-one");
        var kinds = Enum.GetValues<GovernedLoopNodeKind>()
            .Where(value => value != GovernedLoopNodeKind.Unknown)
            .ToArray();
        var nodes = kinds.Select((kind, index) => new GovernedLoopNodeDefinition(
            kind.ToString().ToLowerInvariant(),
            new GovernedLoopNodeDescriptor(
                kind,
                kind == GovernedLoopNodeKind.HumanInput ? GovernedLoopHumanInputVocabulary.TypeId : kind.ToString().ToLowerInvariant() + "-type",
                1),
            kind switch
            {
                GovernedLoopNodeKind.Trigger =>
                [
                    new GovernedLoopPortDefinition("context-out", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context, "text", true),
                    new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                ],
                GovernedLoopNodeKind.Inference =>
                [
                    new GovernedLoopPortDefinition("context-in", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context, "text", true),
                    new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                    new GovernedLoopPortDefinition("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                ],
                GovernedLoopNodeKind.Exit =>
                [
                    new GovernedLoopPortDefinition("published", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                    new GovernedLoopPortDefinition("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                ],
                GovernedLoopNodeKind.HumanInput =>
                [
                    new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, configuration.RequestSchemaReference!, true),
                ],
                _ => [],
            },
            GovernedLoopAuthorityCeiling.Create(kind == GovernedLoopNodeKind.Inference ? [ModelInferenceCapabilityId] : []),
            kind == GovernedLoopNodeKind.HumanInput
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["ordinal"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            null,
            null,
            null,
            kind == GovernedLoopNodeKind.HumanInput ? configuration : null))
            .ToArray();
        var display = nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(
            node.Id,
            node.Descriptor.Kind.ToString(),
            "Closed schema-one node.",
            index * 10,
            index * 20)).ToArray();
        var conditions = Enum.GetValues<GovernedLoopControlCondition>()
            .Where(value => value != GovernedLoopControlCondition.Unknown)
            .Select(condition => new GovernedLoopControlEdgeDefinition(
                "edge-" + condition.ToString().ToLowerInvariant(),
                "trigger",
                "exit",
                condition))
            .ToArray();
        return GovernedLoopGraphDefinition.Create(
            1,
            "all-enums-graph",
            "all-enums-revision",
            "Round-trip every closed schema-one graph discriminator.",
            Role(),
            "trigger",
            ["exit", "fail"],
            GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId]),
            [
                new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false),
                new GovernedLoopValueSchemaDefinition("boolean", GovernedLoopValueKind.Boolean, !requireBooleanNonNullable),
                new GovernedLoopValueSchemaDefinition("integer", GovernedLoopValueKind.Integer, false),
                new GovernedLoopValueSchemaDefinition("number", GovernedLoopValueKind.Number, false),
                new GovernedLoopValueSchemaDefinition("object", GovernedLoopValueKind.Object, false),
                new GovernedLoopValueSchemaDefinition("array", GovernedLoopValueKind.Array, false, ElementSchemaId: "text"),
                new GovernedLoopValueSchemaDefinition("binary", GovernedLoopValueKind.Binary, false, "base64"),
            ],
            nodes,
            conditions,
            [
                new GovernedLoopBindingDefinition("context", GovernedLoopBindingKind.Context, "trigger", "context-out", "inference", "context-in"),
                new GovernedLoopBindingDefinition("request", GovernedLoopBindingKind.Data, "trigger", "request", "inference", "request"),
                new GovernedLoopBindingDefinition("result", GovernedLoopBindingKind.Data, "inference", "result", "exit", "result"),
            ],
            new GovernedLoopOutputContract(
                "Return the result.",
                [new GovernedLoopOutputDefinition("result", "text", "exit", "published", true)]),
            new GovernedLoopDisplayMetadata("All enums", "Every closed discriminator.", display),
            GovernedLoopGraphTestFixture.DefaultModelRoutingPolicy());
    }

    private static GovernedLoopHumanInputNodeConfiguration HumanInputConfiguration(
        string requestSchemaReference,
        HumanInputPrivacyClass privacyClass,
        HumanInputResponseSchema responseSchema,
        HumanInputResponsePolicy responsePolicy)
        => new(
            GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion,
            requestSchemaReference,
            "Collect untrusted data.",
            "Provide a bounded response.",
            responseSchema,
            privacyClass,
            [new HumanInputEligibleRespondent("user-one", "role-one", "route-one"), new HumanInputEligibleRespondent("user-two", "role-two", "route-two")],
            responsePolicy,
            "timeout-policy-one",
            "failure-policy-one");

    private static ContextualRoleRevisionPin Role(
        string roleId = "researcher",
        int revision = 1,
        char contentHash = 'a')
        => new(new ContextualRoleRevisionIdentity(roleId, revision), new string(contentHash, 64));

    private static ContextualRoleRevisionPin ValidRolePin()
    {
        var role = ValidRoleRevision();
        return new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
    }

    private static ContextualRoleRevision ValidRoleRevision()
    {
        var role = new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity("researcher", 1),
            string.Empty,
            "Researcher",
            "Answer one bounded request.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("actor-one", _time.AddHours(-2), _time.AddHours(-1)),
            new ContextualRoleWorkspaceApplicability([_workspaceId]),
            new ContextualRoleInstructionSourceReference(
                ContextualRoleInstructionSourceKind.RoleArtifact,
                "researcher-source",
                ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(ImmutableArray.Create(ModelInferenceCapabilityId)));
        return ContextualRoleRevisionContentHash.Apply(role);
    }

    private static string ReplaceOwningRole(string json, string newline, string replacement)
    {
        const string StartMarker = "    \"owningRole\": {";
        var endMarker = "    }," + newline + "    \"entryNodeId\"";
        var start = json.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = json.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return json[..start] + "    " + replacement + "," + newline + "    \"entryNodeId\"" + json[(end + endMarker.Length)..];
    }

    private static GovernedLoopDisplayMetadata Display(string name, int x, int y)
        => new(
            name,
            "Display-only authoring metadata.",
            [
                new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Collect input.", x, y),
                new GovernedLoopNodeDisplayMetadata("infer", "Inference", "Answer.", x + 100, y),
                new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Publish.", x + 200, y),
            ]);

    private static GovernedLoopPortDefinition InputPort(string id)
        => new(id, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true);

    private static GovernedLoopPortDefinition OutputPort(string id)
        => new(id, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true);

    private static string GraphRoot(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "revisions", "graph-authoring");

    private static string ArtifactPath(WorkspacePaths paths, GovernedLoopGraphDefinition graph)
        => Path.Combine(GraphRoot(paths), "artifacts", graph.GraphId, graph.RevisionId + ".json");

    private static async Task<string> PersistedContentDigestAsync(GovernedLoopGraphDefinition graph)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var committed = await Store(paths, new TestCapabilityLifecycleTrustProvider())
            .CommitAsync(CreateDraft(graph, "create-digest", HashA, HashB, 0, _time));
        Assert.Equal(GovernedLoopRevisionStoreCommitStatus.Committed, committed.Status);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(ArtifactPath(paths, graph)));
        return document.RootElement.GetProperty("contentDigest").GetString()!;
    }

    private static string ImmutableReadyPath(string destinationPath, byte[] content)
    {
        var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{digest}.ready");
    }

    private static async Task<long> IntentTrustGenerationAsync(WorkspacePaths paths, string operationId)
    {
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(GraphRoot(paths), "operations", operationId + ".json"));
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("trustGeneration").GetInt64();
    }

    private sealed class FixedNodeCatalog(GovernedLoopNodeCatalogSnapshot snapshot) : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class FixedAuthorityProvider(GovernedLoopAuthoritySnapshot snapshot) : IGovernedLoopAuthoritySnapshotProvider
    {
        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(ContextualRoleRevisionPin? owningRole, CancellationToken cancellationToken = default)
        {
            _ = owningRole;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class AuthorizedActor : IGovernedLoopRevisionActorAuthorizer
    {
        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(
            GovernedLoopRevisionActorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GovernedLoopRevisionActorAuthorization(
                GovernedLoopRevisionActorAuthorizationStatus.Authorized,
                request.Request.OperationId,
                request.RequestHash,
                request.Request.ActorId,
                HashA));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingDestinationDurabilityBarrier(
        string destination,
        int failureCount = 1) : ICapabilityCatalogDurabilityBarrier
    {
        public int TargetFlushCount { get; private set; }

        public void BeforeDirectoryMove(string stagingPath, string destinationPath)
        {
            _ = stagingPath;
            _ = destinationPath;
        }

        public void AfterDirectoryMove(string stagingPath, string destinationPath)
        {
            _ = stagingPath;
            _ = destinationPath;
        }

        public void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory)
        {
            _ = directoryPath;
            _ = parentDirectory;
        }

        public ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
        {
            _ = parentDirectory;
            if (!string.Equals(destinationPath, destination, PathComparison()))
            {
                return ValueTask.CompletedTask;
            }

            TargetFlushCount++;
            return TargetFlushCount <= failureCount
                ? ValueTask.FromException(new IOException("Injected final-destination durability failure."))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class CompetingExactDestinationDurabilityBarrier(string destination) : ICapabilityCatalogDurabilityBarrier
    {
        public bool InjectedCompetingDestination { get; private set; }

        public int TargetFlushCount { get; private set; }

        public void BeforeDirectoryMove(string stagingPath, string destinationPath)
        {
            _ = stagingPath;
            _ = destinationPath;
        }

        public void AfterDirectoryMove(string stagingPath, string destinationPath)
        {
            _ = stagingPath;
            _ = destinationPath;
        }

        public void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory)
        {
            _ = directoryPath;
            _ = parentDirectory;
        }

        public ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
        {
            _ = parentDirectory;
            if (!InjectedCompetingDestination
                && destinationPath.EndsWith(".ready", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(destinationPath), Path.GetDirectoryName(destination), PathComparison())
                && Path.GetFileName(destinationPath).StartsWith(
                    "." + Path.GetFileName(destination) + ".",
                    StringComparison.Ordinal))
            {
                File.Copy(destinationPath, destination);
                InjectedCompetingDestination = true;
                return ValueTask.CompletedTask;
            }

            if (!string.Equals(destinationPath, destination, PathComparison()))
            {
                return ValueTask.CompletedTask;
            }

            TargetFlushCount++;
            return TargetFlushCount == 1
                ? ValueTask.FromException(new IOException("Injected competing-destination durability failure."))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class HardLinkingPublicationObserver(
        string destination,
        string external,
        string retained) : ICapabilityCatalogPathObserver
    {
        public bool Substituted { get; private set; }

        public void BeforeDirectoryChildOpen(string parentPath, string childName)
        {
            _ = parentPath;
            _ = childName;
        }

        public void BeforeFileChildOpen(string parentPath, string childName)
        {
            if (Substituted
                || !string.Equals(Path.Combine(parentPath, childName), destination, PathComparison())
                || !File.Exists(destination))
            {
                return;
            }

            File.Move(destination, retained);
            CreateHardLink(destination, external);
            Substituted = true;
        }

        public void AfterFileChildOpen(string parentPath, string childName)
        {
            _ = parentPath;
            _ = childName;
        }
    }

    private static void CreateHardLink(string linkPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(WindowsCreateHardLink(linkPath, existingPath, IntPtr.Zero));
            return;
        }

        Assert.Equal(0, UnixCreateHardLink(existingPath, linkPath));
    }

    private static StringComparison PathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool WindowsCreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int UnixCreateHardLink(string existingPath, string newPath);
}
