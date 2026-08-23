using System.Collections.Immutable;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class GovernedLoopGraphAuthoringFactoryTests
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string ModelProfileCapabilityId = "org.embodysense/model-profile/codex";
    private const string WorkspaceReadCapabilityId = "org.embodysense/workspace-read";
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', ContextualRoleLimits.Sha256HexCharacters);

    [Fact]
    public async Task Concrete_factory_persists_create_and_exactly_replays_after_restart_without_starting_runtime()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var candidate = Candidate();
        var normalized = GovernedLoopGraphNormalizer.Normalize(candidate);
        Assert.True(normalized.IsValid);
        var reference = normalized.Graph!.RevisionReference;
        var request = new GovernedLoopGraphAuthoringRequest(
            1,
            new GovernedLoopRevisionLifecycleRequest(
                1,
                "create-research-loop",
                GovernedLoopRevisionOperationKind.CreateDraft,
                reference.GraphId,
                Actor(),
                GovernedLoopRevisionLifecycleStatus.Unknown,
                0,
                null,
                null,
                reference,
                null,
                null),
            candidate);
        var catalog = new RecordingNodeCatalog(Catalog(candidate));
        var authority = new RecordingAuthorityProvider(Authority());
        var authorizer = new RecordingActorAuthorizer();

        var created = await GovernedLoopGraphAuthoringFactory.Create(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            catalog,
            authority,
            authorizer,
            new FixedTimeProvider(_now)).MutateAsync(request);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, created.Status);
        Assert.Equal(GovernedLoopGraphRevisionChangeKind.Initial, created.ChangeKind);
        Assert.NotNull(created.RevisionIdentity);
        Assert.NotNull(created.LifecycleResult?.Evidence);
        Assert.Equal(1, catalog.Calls);
        Assert.Equal(1, authority.Calls);
        Assert.Equal(2, authorizer.Calls);

        var restartCatalog = new RecordingNodeCatalog(Catalog(candidate));
        var restartAuthority = new RecordingAuthorityProvider(Authority());
        var restartAuthorizer = new RecordingActorAuthorizer();
        var replayed = await GovernedLoopGraphAuthoringFactory.Create(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            restartCatalog,
            restartAuthority,
            restartAuthorizer,
            new FixedTimeProvider(_now.AddDays(1))).MutateAsync(request);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Replayed, replayed.Status);
        Assert.Equal(created.RevisionIdentity, replayed.RevisionIdentity);
        Assert.Equal(created.LifecycleResult.Evidence, replayed.LifecycleResult?.Evidence);
        Assert.Equal(0, restartCatalog.Calls);
        Assert.Equal(0, restartAuthority.Calls);
        Assert.Equal(0, restartAuthorizer.Calls);
        Assert.True(Directory.Exists(Path.Combine(paths.AgentPath, "loops", "revisions", "graph-authoring")));
        Assert.False(Directory.Exists(paths.LoopRunsPath));
        Assert.False(Directory.Exists(paths.CustomLoopDefinitionsPath));
    }

    [Fact]
    public async Task Factory_reuses_the_exact_supplied_authority_transaction()
    {
        var transaction = new RecordingAuthorityTransaction();

        var service = GovernedLoopGraphAuthoringFactory.Create(
            new UnusedRevisionStore(),
            new UnusedNodeCatalog(),
            new UnusedAuthorityProvider(),
            new UnusedActorAuthorizer(),
            transaction);
        var result = await service.MutateAsync(null);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Invalid, result.Status);
        Assert.Equal(1, transaction.ExecuteCount);
    }

    [Fact]
    public void Production_default_factory_composes_without_touching_workspace_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var service = GovernedLoopGraphAuthoringFactory.Create(
            paths,
            new UnusedAuthorityProvider(),
            new UnusedActorAuthorizer());

        Assert.NotNull(service);
        Assert.False(Directory.Exists(paths.AgentPath));
    }

    [Fact]
    public async Task Built_in_catalog_admits_runtime_executable_linear_pure_topology_and_bounded_cycle_graphs()
    {
        var candidates = new (string Name, GovernedLoopGraphCandidate Candidate)[]
        {
            ("linear", RuntimeExecutable(Candidate())),
            ("transform", RuntimeExecutable(IdentityTransformCandidate())),
            ("validate", RuntimeExecutable(SchemaConformanceCandidate())),
            ("transform-validate", RuntimeExecutable(TransformValidateCandidate())),
            ("condition-join", RuntimeExecutable(ConditionJoinCandidate())),
            ("bounded-cycle", RuntimeExecutable(BoundedCycleCandidate())),
        };

        foreach (var (name, candidate) in candidates)
        {
            var result = await AuthorWithBuiltInCatalogAsync(candidate);

            Assert.True(
                result.Status == GovernedLoopGraphAuthoringStatus.Committed,
                $"The built-in catalog rejected the {name} graph:{Environment.NewLine}{string.Join(Environment.NewLine, result.GraphValidationErrors.Select(error => $"{error.Code}: {error.Element.Path}"))}");
            Assert.Matches("^[0-9a-f]{64}$", result.GraphValidationEvidenceHash);
            var plan = GovernedLoopSequentialPlanBuilder.Build(Artifact(candidate));
            Assert.True(
                plan.Status == GovernedLoopSequentialPlanBuildStatus.Ready,
                $"The built-in catalog admitted the {name} graph, but the executable runtime rejected `{plan.FailurePath}` with `{plan.Status}`.");
        }
    }

    [Fact]
    public async Task Built_in_catalog_advertises_and_admits_each_application_owned_wait_descriptor()
    {
        Assert.Equal(2, GovernedLoopWaitNodeCatalogContract.Descriptors.Count);

        foreach (var descriptor in GovernedLoopWaitNodeCatalogContract.Descriptors)
        {
            var candidate = RuntimeExecutable(WaitCandidate(descriptor));
            var result = await AuthorWithBuiltInCatalogAsync(candidate);

            Assert.True(
                result.Status == GovernedLoopGraphAuthoringStatus.Committed,
                $"The built-in catalog rejected `{descriptor.Descriptor.TypeId}`:{Environment.NewLine}{string.Join(Environment.NewLine, result.GraphValidationErrors.Select(error => $"{error.Code}: {error.Element.Path}"))}");
            var plan = GovernedLoopSequentialPlanBuilder.Build(Artifact(candidate));
            Assert.True(
                plan.Status == GovernedLoopSequentialPlanBuildStatus.Ready,
                $"The sequential builder rejected `{descriptor.Descriptor.TypeId}` at `{plan.FailurePath}` with `{plan.Status}`.");
        }
    }

    [Fact]
    public async Task Built_in_catalog_advertises_and_admits_each_exact_workspace_action_descriptor()
    {
        var descriptors = new[]
        {
            WorkspaceActionNodeDescriptors.Append,
            WorkspaceActionNodeDescriptors.Write,
            WorkspaceActionNodeDescriptors.Delete,
        };

        foreach (var descriptor in descriptors)
        {
            var candidate = WorkspaceActionCandidate(descriptor);
            var result = await AuthorWithBuiltInCatalogAsync(candidate);

            Assert.True(
                result.Status == GovernedLoopGraphAuthoringStatus.Committed,
                $"The built-in catalog rejected `{descriptor.TypeId}`:{Environment.NewLine}{string.Join(Environment.NewLine, result.GraphValidationErrors.Select(error => $"{error.Code}: {error.Element.Path}"))}");
            var plan = GovernedLoopSequentialPlanBuilder.Build(Artifact(candidate));
            Assert.True(
                plan.Status == GovernedLoopSequentialPlanBuildStatus.Ready,
                $"The sequential builder rejected `{descriptor.TypeId}` at `{plan.FailurePath}` with `{plan.Status}`.");
        }
    }

    [Fact]
    public async Task Built_in_catalog_snapshot_has_one_deterministic_full_contract_hash()
    {
        var first = await AuthorWithBuiltInCatalogAsync(Candidate());
        var second = await AuthorWithBuiltInCatalogAsync(Candidate());

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, first.Status);
        Assert.Equal(first.GraphValidationEvidenceHash, second.GraphValidationEvidenceHash);
        Assert.Equal("557783ab186ee6267f901308f6ce729bfbc95a829865b578ca7920985b26fc9f", first.GraphValidationEvidenceHash);
    }

    [Fact]
    public async Task Built_in_baseline_catalog_pins_contracts_and_requires_its_exact_execution_envelope()
    {
        const int TriggerEvidenceItems = 1;
        const int DispatchedEvidenceItems = CustomLoopLimits.MaxGraphSequentialEvidenceItemsPerActivation;
        var linear = Candidate();
        var portMismatch = linear with
        {
            Nodes = linear.Nodes!.Select(node => node!.Id == "trigger"
                ? node with
                {
                    Ports = node.Ports.Select(port => port.Id == "request" ? port with { Required = false } : port).ToArray(),
                }
                : node).ToArray(),
        };
        var missingInstruction = linear with
        {
            Nodes = linear.Nodes!.Select(node => node!.Id == "infer"
                ? node with { Parameters = new Dictionary<string, string>() }
                : node).ToArray(),
        };
        var missingExitCapability = linear with
        {
            Nodes = linear.Nodes!.Select(node => node!.Id == "exit"
                ? node with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create([]) }
                : node).ToArray(),
        };

        var portResult = await AuthorWithBuiltInCatalogAsync(portMismatch);
        var parameterResult = await AuthorWithBuiltInCatalogAsync(missingInstruction);
        var capabilityResult = await AuthorWithBuiltInCatalogAsync(missingExitCapability);
        var zeroEnvelopeResult = await AuthorWithBuiltInCatalogAsync(linear, Authority() with
        {
            MaxAttempts = 0,
            MaxPayloadCharacters = 0,
            MaxEvidenceItems = 0,
            MaxResourceUnits = 0,
        });
        var exactEnvelope = Authority() with
        {
            MaxAttempts = 3,
            MaxPayloadCharacters = 0,
            MaxEvidenceItems = TriggerEvidenceItems + (2 * DispatchedEvidenceItems),
            MaxResourceUnits = 1,
        };
        var exactEnvelopeResult = await AuthorWithBuiltInCatalogAsync(linear, exactEnvelope);
        var insufficientAttempts = await AuthorWithBuiltInCatalogAsync(linear, exactEnvelope with { MaxAttempts = 2 });
        var insufficientEvidence = await AuthorWithBuiltInCatalogAsync(linear, exactEnvelope with { MaxEvidenceItems = exactEnvelope.MaxEvidenceItems - 1 });
        var insufficientProviderUnits = await AuthorWithBuiltInCatalogAsync(linear, exactEnvelope with { MaxResourceUnits = 0 });

        Assert.Contains(portResult.GraphValidationErrors, error => error.Code == "node.port-contract.incompatible" && error.Element.Id == "trigger.request");
        Assert.Contains(parameterResult.GraphValidationErrors, error => error.Code == "node.parameter.required" && error.Element.Id == "infer");
        Assert.Contains(capabilityResult.GraphValidationErrors, error => error.Code == "node.authority.missing-capability" && error.Element.Id == "exit");
        Assert.Equal(GovernedLoopGraphAuthoringStatus.ValidationRejected, zeroEnvelopeResult.Status);
        Assert.Contains(zeroEnvelopeResult.GraphValidationErrors, error => error.Code == "graph.resources.attempts");
        Assert.Contains(zeroEnvelopeResult.GraphValidationErrors, error => error.Code == "graph.resources.evidence");
        Assert.Contains(zeroEnvelopeResult.GraphValidationErrors, error => error.Code == "graph.resources.units");
        Assert.DoesNotContain(zeroEnvelopeResult.GraphValidationErrors, error => error.Code == "graph.resources.payload");
        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, exactEnvelopeResult.Status);
        Assert.Contains(insufficientAttempts.GraphValidationErrors, error => error.Code == "graph.resources.attempts");
        Assert.Contains(insufficientEvidence.GraphValidationErrors, error => error.Code == "graph.resources.evidence");
        Assert.Contains(insufficientProviderUnits.GraphValidationErrors, error => error.Code == "graph.resources.units");
    }

    [Fact]
    public async Task Built_in_topology_catalog_rejects_over_limit_cycle_budget()
    {
        var candidate = BoundedCycleCandidate();
        candidate = candidate with
        {
            Nodes = candidate.Nodes!.Select(node => node!.Id == "condition"
                ? node with
                {
                    Parameters = new Dictionary<string, string>(node.Parameters, StringComparer.Ordinal)
                    {
                        [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] =
                            (CustomLoopLimits.MaxGraphCycleIterations + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                }
                : node).ToArray(),
        };

        var result = await AuthorWithBuiltInCatalogAsync(candidate);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.ValidationRejected, result.Status);
        Assert.Contains(result.GraphValidationErrors, error => error.Code == "node.parameter.incompatible" || error.Code == "node.cycle.iteration-budget");
    }

    [Fact]
    public async Task Explicit_catalog_remains_authoritative_without_built_in_fallback()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var catalog = new RecordingNodeCatalog(new GovernedLoopNodeCatalogSnapshot(true, "explicit-empty-catalog", []));
        var service = GovernedLoopGraphAuthoringFactory.Create(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            catalog,
            new RecordingAuthorityProvider(Authority()),
            new RecordingActorAuthorizer(),
            new FixedTimeProvider(_now));

        var result = await service.MutateAsync(CreateRequest(Candidate()));

        Assert.Equal(GovernedLoopGraphAuthoringStatus.ValidationRejected, result.Status);
        Assert.Equal(1, catalog.Calls);
        Assert.Contains(result.GraphValidationErrors, error => error.Code == "node.descriptor.not-advertised");
    }

    [Fact]
    public async Task Built_in_catalog_composition_honors_pre_cancelled_authoring_without_resolving_authority()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var authority = new RecordingAuthorityProvider(Authority());
        var service = GovernedLoopGraphAuthoringFactory.Create(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            authority,
            new RecordingActorAuthorizer(),
            new FixedTimeProvider(_now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.MutateAsync(CreateRequest(Candidate()), cancellation.Token));

        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public void Factory_rejects_missing_server_owned_dependencies()
    {
        var store = new UnusedRevisionStore();
        var catalog = new UnusedNodeCatalog();
        var authority = new UnusedAuthorityProvider();
        var authorizer = new UnusedActorAuthorizer();
        var transaction = new RecordingAuthorityTransaction();

        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, catalog, authority, authorizer, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, null!, authority, authorizer, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, catalog, null!, authorizer, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, catalog, authority, null!, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, catalog, authority, authorizer, null!));
    }

    [Fact]
    public void Concrete_factory_rejects_missing_server_owned_dependencies()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var catalog = new UnusedNodeCatalog();
        var authority = new UnusedAuthorityProvider();
        var authorizer = new UnusedActorAuthorizer();

        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, null!, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, authority, null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, catalog, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, trust, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, trust, null!, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, trust, authority, null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, trust, catalog, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, null!, catalog, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, trust, null!, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, trust, catalog, null!, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, trust, catalog, authority, null!));
    }

    private static async Task<GovernedLoopGraphAuthoringResult> AuthorWithBuiltInCatalogAsync(
        GovernedLoopGraphCandidate candidate,
        GovernedLoopAuthoritySnapshot? authority = null)
    {
        using var workspace = new TestWorkspace();
        var service = GovernedLoopGraphAuthoringFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            new RecordingAuthorityProvider(authority ?? Authority()),
            new RecordingActorAuthorizer(),
            new FixedTimeProvider(_now));
        return await service.MutateAsync(CreateRequest(candidate));
    }

    private static GovernedLoopGraphAuthoringRequest CreateRequest(GovernedLoopGraphCandidate candidate)
    {
        var normalized = GovernedLoopGraphNormalizer.Normalize(candidate);
        Assert.True(normalized.IsValid);
        var reference = normalized.Graph!.RevisionReference;
        return new GovernedLoopGraphAuthoringRequest(
            1,
            new GovernedLoopRevisionLifecycleRequest(
                1,
                $"create-{reference.GraphId}",
                GovernedLoopRevisionOperationKind.CreateDraft,
                reference.GraphId,
                Actor(),
                GovernedLoopRevisionLifecycleStatus.Unknown,
                0,
                null,
                null,
                reference,
                null,
                null),
            candidate);
    }

    private static GovernedLoopGraphCandidate IdentityTransformCandidate()
    {
        var baseline = Nodes();
        var identity = new GovernedLoopNodeDefinition(
            "identity",
            GovernedLoopSequentialNodeDescriptors.IdentityTransform,
            [
                Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        return GraphCandidate(
            "identity-transform-loop",
            [baseline[0], identity, baseline[1], baseline[2]],
            [
                new("trigger-to-identity", "trigger", "identity", GovernedLoopControlCondition.Always),
                new("identity-to-infer", "identity", "infer", GovernedLoopControlCondition.Success),
                new("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new("request-to-identity", GovernedLoopBindingKind.Data, "trigger", "request", "identity", GovernedLoopPureNodeVocabulary.InputPort),
                new("identity-to-request", GovernedLoopBindingKind.Data, "identity", GovernedLoopPureNodeVocabulary.OutputPort, "infer", "request"),
                new("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            [new("text", GovernedLoopValueKind.Text, false)]);
    }

    private static GovernedLoopGraphCandidate SchemaConformanceCandidate()
    {
        var baseline = Nodes();
        var validation = new GovernedLoopNodeDefinition(
            "schema-check",
            GovernedLoopSequentialNodeDescriptors.SchemaConformance,
            [
                Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port(GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "boolean"),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        return GraphCandidate(
            "schema-conformance-loop",
            [baseline[0], validation, baseline[1], baseline[2]],
            [
                new("trigger-to-schema", "trigger", "schema-check", GovernedLoopControlCondition.Always),
                new("schema-to-infer", "schema-check", "infer", GovernedLoopControlCondition.Success),
                new("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new("request-to-schema", GovernedLoopBindingKind.Data, "trigger", "request", "schema-check", GovernedLoopPureNodeVocabulary.InputPort),
                new("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            [
                new("boolean", GovernedLoopValueKind.Boolean, false),
                new("text", GovernedLoopValueKind.Text, false),
            ]);
    }

    private static GovernedLoopGraphCandidate TransformValidateCandidate()
    {
        var baseline = Nodes();
        var identity = new GovernedLoopNodeDefinition(
            "identity",
            GovernedLoopSequentialNodeDescriptors.IdentityTransform,
            [
                Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        var validation = new GovernedLoopNodeDefinition(
            "schema-check",
            GovernedLoopSequentialNodeDescriptors.SchemaConformance,
            [
                Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port(GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "boolean"),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        return GraphCandidate(
            "transform-validate-loop",
            [baseline[0], identity, validation, baseline[1], baseline[2]],
            [
                new("trigger-to-identity", "trigger", "identity", GovernedLoopControlCondition.Always),
                new("identity-to-schema", "identity", "schema-check", GovernedLoopControlCondition.Success),
                new("schema-to-infer", "schema-check", "infer", GovernedLoopControlCondition.Success),
                new("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new("request-to-identity", GovernedLoopBindingKind.Data, "trigger", "request", "identity", GovernedLoopPureNodeVocabulary.InputPort),
                new("identity-output-to-schema", GovernedLoopBindingKind.Data, "identity", GovernedLoopPureNodeVocabulary.OutputPort, "schema-check", GovernedLoopPureNodeVocabulary.InputPort),
                new("identity-to-request", GovernedLoopBindingKind.Data, "identity", GovernedLoopPureNodeVocabulary.OutputPort, "infer", "request"),
                new("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            [
                new("boolean", GovernedLoopValueKind.Boolean, false),
                new("text", GovernedLoopValueKind.Text, false),
            ]);
    }

    private static GovernedLoopGraphCandidate ConditionJoinCandidate()
    {
        var baseline = Nodes();
        var condition = new GovernedLoopNodeDefinition(
            "condition",
            GovernedLoopSequentialNodeDescriptors.ExactTextCondition,
            [Port(GovernedLoopTopologyNodeVocabulary.ValuePort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string> { [GovernedLoopTopologyNodeVocabulary.ExpectedParameter] = "take-true" });
        var join = new GovernedLoopNodeDefinition(
            "join",
            GovernedLoopSequentialNodeDescriptors.SelectedJoin,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        return GraphCandidate(
            "condition-join-loop",
            [baseline[0], baseline[1], condition, join, baseline[2]],
            [
                new("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new("infer-to-condition", "infer", "condition", GovernedLoopControlCondition.Success),
                new("condition-true-to-join", "condition", "join", GovernedLoopControlCondition.True),
                new("condition-false-to-join", "condition", "join", GovernedLoopControlCondition.False),
                new("join-to-exit", "join", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new("result-to-condition", GovernedLoopBindingKind.Data, "infer", "result", "condition", GovernedLoopTopologyNodeVocabulary.ValuePort),
                new("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            [new("text", GovernedLoopValueKind.Text, false)]);
    }

    private static GovernedLoopGraphCandidate BoundedCycleCandidate()
    {
        var baseline = Nodes();
        var condition = new GovernedLoopNodeDefinition(
            "condition",
            GovernedLoopSequentialNodeDescriptors.ExactTextCondition,
            [Port(GovernedLoopTopologyNodeVocabulary.ValuePort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>
            {
                [GovernedLoopTopologyNodeVocabulary.ExpectedParameter] = "repeat",
                [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "2",
                [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "5000",
            });
        return GraphCandidate(
            "bounded-cycle-loop",
            [baseline[0], baseline[1], condition, baseline[2]],
            [
                new("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new("infer-to-condition", "infer", "condition", GovernedLoopControlCondition.Success),
                new("condition-repeat", "condition", "condition", GovernedLoopControlCondition.True),
                new("condition-exit", "condition", "exit", GovernedLoopControlCondition.False),
            ],
            [
                new("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new("result-to-condition", GovernedLoopBindingKind.Data, "infer", "result", "condition", GovernedLoopTopologyNodeVocabulary.ValuePort),
                new("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            [new("text", GovernedLoopValueKind.Text, false)]);
    }

    private static GovernedLoopGraphCandidate WaitCandidate(GovernedLoopNodeCatalogDescriptor descriptor)
    {
        var baseline = Nodes();
        var parameter = Assert.Single(descriptor.Parameters);
        var parameterValue = descriptor.Descriptor.TypeId == GovernedLoopWaitVocabulary.Timestamp
            ? _now.AddMinutes(5).ToString(GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat, System.Globalization.CultureInfo.InvariantCulture)
            : "authenticated-event-1";
        var wait = new GovernedLoopNodeDefinition(
            "wait",
            descriptor.Descriptor,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>(StringComparer.Ordinal) { [parameter.Id] = parameterValue });
        return GraphCandidate(
            $"{descriptor.Descriptor.TypeId}-loop",
            [baseline[0], wait, baseline[1], baseline[2]],
            [
                new("trigger-to-wait", "trigger", "wait", GovernedLoopControlCondition.Always),
                new("wait-to-infer", "wait", "infer", GovernedLoopControlCondition.Success),
                new("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            [new("text", GovernedLoopValueKind.Text, false)]);
    }

    private static GovernedLoopGraphCandidate WorkspaceActionCandidate(GovernedLoopNodeDescriptor descriptor)
    {
        var baseline = Nodes();
        var input = descriptor == WorkspaceActionNodeDescriptors.Delete
            ? "{\"precondition\":{\"expectedContentHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"kind\":\"expectedContentHash\"},\"schemaVersion\":1,\"scopeId\":\"workspace\",\"segments\":[],\"target\":\"notes.txt\"}"
            : "{\"precondition\":{\"kind\":\"expectedAbsent\"},\"schemaVersion\":1,\"scopeId\":\"workspace\",\"segments\":[{\"kind\":\"literalUtf8\",\"literal\":\"hello\"}],\"target\":\"notes.txt\"}";
        var action = new GovernedLoopNodeDefinition(
            "workspace-action",
            descriptor,
            [Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([WorkspaceCommandCapabilityId]),
            new Dictionary<string, string> { ["input"] = input });
        return new GovernedLoopGraphCandidate(
            1,
            descriptor.TypeId + "-loop",
            "revision-1",
            "Execute one exact governed workspace Action.",
            RolePin(),
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceCommandCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            [baseline[0], baseline[1], action, baseline[2]],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-action", "infer", "workspace-action", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("action-to-exit", "workspace-action", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new GovernedLoopBindingDefinition("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new GovernedLoopBindingDefinition("action-result-to-exit", GovernedLoopBindingKind.Data, "workspace-action", "result", "exit", "result"),
            ],
            new GovernedLoopOutputContract("Return the result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                descriptor.TypeId + " loop",
                "Catalog composition test.",
                new[] { baseline[0], baseline[1], action, baseline[2] }.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Exact catalog node.", index * 100, 0)).ToArray()),
            EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());
    }

    private static GovernedLoopGraphCandidate GraphCandidate(
        string graphId,
        IReadOnlyList<GovernedLoopNodeDefinition> nodes,
        IReadOnlyList<GovernedLoopControlEdgeDefinition> edges,
        IReadOnlyList<GovernedLoopBindingDefinition> bindings,
        IReadOnlyList<GovernedLoopValueSchemaDefinition> schemas)
        => new(
            1,
            graphId,
            "revision-1",
            "Exercise one exact built-in governed-loop catalog composition.",
            RolePin(),
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceReadCapabilityId]),
            schemas.Cast<GovernedLoopValueSchemaDefinition?>().ToArray(),
            nodes.Cast<GovernedLoopNodeDefinition?>().ToArray(),
            edges.Cast<GovernedLoopControlEdgeDefinition?>().ToArray(),
            bindings.Cast<GovernedLoopBindingDefinition?>().ToArray(),
            new GovernedLoopOutputContract("Return the result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                graphId,
                "Catalog composition test.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Exact catalog node.", index * 100, 0)).ToArray()),
            EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());

    private static GovernedLoopGraphCandidate Candidate()
        => new(
            1,
            "research-loop",
            "revision-1",
            "Research one question safely.",
            RolePin(),
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceReadCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            Nodes(),
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("request-binding", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new GovernedLoopBindingDefinition("context-binding", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new GovernedLoopBindingDefinition("result-binding", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            new GovernedLoopOutputContract("Return the result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Research loop",
                "Display only.",
                [
                    new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Start.", 0, 0),
                    new GovernedLoopNodeDisplayMetadata("infer", "Inference", "Answer.", 100, 0),
                    new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Finish.", 200, 0),
                ]),
            EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());

    private static GovernedLoopNodeDefinition[] Nodes()
        =>
        [
            new("trigger", new(GovernedLoopNodeKind.Trigger, "manual-trigger", 1), [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
            new("infer", new(GovernedLoopNodeKind.Inference, "provider-inference", 1), [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId, ModelProfileCapabilityId]), new Dictionary<string, string> { ["instruction"] = "Answer safely." }),
            new("exit", new(GovernedLoopNodeKind.Exit, "success-exit", 1), [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]), new Dictionary<string, string>()),
        ];

    private static GovernedLoopPortDefinition Port(
        string id,
        GovernedLoopPortDirection direction,
        GovernedLoopBindingKind kind,
        string valueSchemaId = "text")
        => new(id, direction, kind, valueSchemaId, true);

    private static GovernedLoopGraphCandidate RuntimeExecutable(GovernedLoopGraphCandidate candidate)
        => candidate with
        {
            AuthorityCeiling = GovernedLoopAuthorityCeiling.Create(
                [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId]),
        };

    private static GovernedLoopGraphRevisionArtifact Artifact(GovernedLoopGraphCandidate candidate)
    {
        var normalized = GovernedLoopGraphNormalizer.Normalize(candidate);
        var graph = Assert.IsType<GovernedLoopGraphDefinition>(normalized.Graph);
        var revision = GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            null,
            null,
            $"create-{graph.GraphId}",
            "actor-1",
            _now);
        return GovernedLoopGraphRevisionArtifactFactory.Create(
            GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion,
            revision,
            graph);
    }

    private static GovernedLoopNodeCatalogSnapshot Catalog(GovernedLoopGraphCandidate candidate)
    {
        var schemas = candidate.ValueSchemas!.Cast<GovernedLoopValueSchemaDefinition>().ToDictionary(schema => schema.Id, schema => schema.Kind, StringComparer.Ordinal);
        var terminals = candidate.TerminalNodeIds!.Cast<string>().ToHashSet(StringComparer.Ordinal);
        return new GovernedLoopNodeCatalogSnapshot(
            true,
            "catalog-1",
            candidate.Nodes!.Cast<GovernedLoopNodeDefinition>().Select(node =>
            {
                var outcomes = candidate.ControlEdges!
                    .Where(edge => edge!.FromNodeId == node.Id)
                    .Select(edge => edge!.Condition)
                    .Distinct()
                    .Order()
                    .ToArray();
                return new GovernedLoopNodeCatalogDescriptor(
                    node.Descriptor,
                    true,
                    true,
                    node.Descriptor.Kind == GovernedLoopNodeKind.Trigger,
                    terminals.Contains(node.Id),
                    outcomes,
                    outcomes,
                    GovernedLoopJoinPolicy.None,
                    0,
                    false,
                    null,
                    null,
                    node.Ports.Select(port => new GovernedLoopCatalogPortContract(port.Id, port.Direction, port.BindingKind, GovernedLoopValueKindSet.Create([schemas[port.ValueSchemaId]]), port.Required)).ToArray(),
                    node.Parameters.Select(parameter => new GovernedLoopCatalogParameterContract(parameter.Key, GovernedLoopParameterValueKind.Text, true, 1, CustomLoopLimits.MaxGraphParameterValueCharacters, null, null, [])).ToArray(),
                    node.AuthorityCeiling.CapabilityIds,
                    new GovernedLoopNodeResourceBudget(0, 0, 0, 0));
            }).ToArray());
    }

    private static GovernedLoopAuthoritySnapshot Authority()
    {
        var role = RoleRevision();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var lifecycle = new ContextualRoleLifecycleSnapshot(
            1,
            role.Identity.RoleId,
            role.Identity,
            ContextualRoleLifecycleState.Active,
            "publish-role",
            ContextualRoleRevisionMutationKind.Create,
            _now.AddMinutes(-10));
        return new GovernedLoopAuthoritySnapshot(
            true,
            Hash('d'),
            pin,
            role,
            lifecycle,
            _workspaceId,
            ContextualRoleInstructionSourceProbeStatus.Ready,
            role.PolicyMaxima.CapabilityIds,
            CustomLoopLimits.MaxGraphAggregateAttempts,
            CustomLoopLimits.MaxGraphAggregatePayloadCharacters,
            CustomLoopLimits.MaxGraphAggregateEvidenceItems,
            CustomLoopLimits.MaxGraphAggregateResourceUnits);
    }

    private static ContextualRoleRevisionPin RolePin()
    {
        var role = RoleRevision();
        return new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
    }

    private static ContextualRoleRevision RoleRevision()
    {
        var role = new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity("researcher", 1),
            string.Empty,
            "Researcher",
            "Research one bounded question.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("actor-1", _now.AddHours(-2), _now.AddHours(-1)),
            new ContextualRoleWorkspaceApplicability([_workspaceId]),
            new ContextualRoleInstructionSourceReference(
                ContextualRoleInstructionSourceKind.RoleArtifact,
                "researcher-source",
                ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(ImmutableArray.Create(ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceCommandCapabilityId, WorkspaceReadCapabilityId)));
        return ContextualRoleRevisionContentHash.Apply(role);
    }

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("actor-1", out var actor, out _));
        return actor!;
    }

    private static string Hash(char value) => new(value, 64);

    private sealed class RecordingNodeCatalog(GovernedLoopNodeCatalogSnapshot snapshot) : IGovernedLoopNodeCatalog
    {
        public int Calls { get; private set; }

        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RecordingAuthorityProvider(GovernedLoopAuthoritySnapshot snapshot) : IGovernedLoopAuthoritySnapshotProvider
    {
        public int Calls { get; private set; }

        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(ContextualRoleRevisionPin? owningRole, CancellationToken cancellationToken = default)
        {
            _ = owningRole;
            Calls++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RecordingActorAuthorizer : IGovernedLoopRevisionActorAuthorizer
    {
        public int Calls { get; private set; }

        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(
            GovernedLoopRevisionActorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new GovernedLoopRevisionActorAuthorization(
                GovernedLoopRevisionActorAuthorizationStatus.Authorized,
                request.Request.OperationId,
                request.RequestHash,
                request.Request.ActorId,
                Hash('a')));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class UnusedRevisionStore : IGovernedLoopGraphRevisionStore
    {
        public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(string graphId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(GovernedLoopRevisionReference revision, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(string graphId, string operationId, string lifecycleRequestHash, string authoringRequestHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(GovernedLoopGraphRevisionStoreMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedNodeCatalog : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedAuthorityProvider : IGovernedLoopAuthoritySnapshotProvider
    {
        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(ContextualRoleRevisionPin? owningRole, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedActorAuthorizer : IGovernedLoopRevisionActorAuthorizer
    {
        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(GovernedLoopRevisionActorAuthorizationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        public int ExecuteCount { get; private set; }

        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return operation(cancellationToken);
        }

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
