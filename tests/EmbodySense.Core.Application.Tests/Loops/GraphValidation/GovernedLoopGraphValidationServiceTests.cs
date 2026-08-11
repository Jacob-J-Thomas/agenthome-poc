using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Tests.Loops.GraphValidation;

public sealed class GovernedLoopGraphValidationServiceTests
{
    private const string ModelInferenceCapability = "org.embodysense/model-inference";
    private const string WorkspaceReadCapability = "org.embodysense/workspace-read";

    [Fact]
    public async Task ValidateReturnsNormalizedGraphAndDeterministicEvidence()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate);
        var first = await Service(descriptors).ValidateAsync(candidate);
        var second = await Service(descriptors.Reverse().ToArray()).ValidateAsync(candidate with
        {
            Nodes = candidate.Nodes!.Reverse().ToArray(),
            ControlEdges = candidate.ControlEdges!.Reverse().ToArray(),
            Bindings = candidate.Bindings!.Reverse().ToArray()
        });

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.NotNull(first.Graph);
        Assert.Equal(first.Graph.ExecutableHash, second.Graph!.ExecutableHash);
        Assert.Equal(first.Evidence, second.Evidence);
        Assert.Equal(64, first.Evidence!.CombinedHash.Length);
    }

    [Fact]
    public async Task ValidateAuthorityEvidenceIsStableAcrossCapabilityEnumerationOrder()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate);
        var forward = await Service(descriptors, Authority()).ValidateAsync(candidate);
        var reversedAuthority = Authority() with
        {
            CapabilityIds = Authority().CapabilityIds.Reverse().ToArray(),
        };

        var reverse = await Service(descriptors, reversedAuthority).ValidateAsync(candidate);

        Assert.True(forward.IsValid);
        Assert.True(reverse.IsValid);
        Assert.Equal(forward.Evidence, reverse.Evidence);
    }

    [Fact]
    public async Task ValidateReturnsIdenticalEvidenceAndErrorsAcrossBoundedPermutations()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Parameters = new Dictionary<string, string> { ["instruction"] = "Answer safely.", ["undeclared"] = "value" } };
        var candidate = Candidate(nodes: nodes);
        var descriptors = Descriptors(Candidate());
        var expected = await Service(descriptors).ValidateAsync(candidate);
        for (var offset = 0; offset < 12; offset++)
        {
            var permuted = candidate with
            {
                Nodes = Rotate(candidate.Nodes!, offset),
                ControlEdges = Rotate(candidate.ControlEdges!, offset).Reverse().ToArray(),
                Bindings = Rotate(candidate.Bindings!, offset + 1)
            };
            var actual = await Service(Rotate(descriptors, offset + 2)).ValidateAsync(permuted);
            Assert.Equal(expected.Evidence, actual.Evidence);
            Assert.Equal(expected.Errors, actual.Errors);
        }
    }

    [Fact]
    public async Task ValidateFailsClosedWhenCatalogOrAuthorityUnavailable()
    {
        var candidate = Candidate();
        var unavailableCatalog = new GovernedLoopGraphValidationService(new FixedCatalog(new GovernedLoopNodeCatalogSnapshot(false, "catalog-1", [])), new FixedAuthority(Authority()));
        var unavailableAuthority = new GovernedLoopGraphValidationService(new FixedCatalog(new GovernedLoopNodeCatalogSnapshot(true, "catalog-1", Descriptors(candidate))), new FixedAuthority(Authority() with { IsAvailable = false }));

        var catalogResult = await unavailableCatalog.ValidateAsync(candidate);
        var authorityResult = await unavailableAuthority.ValidateAsync(candidate);

        Assert.Contains(catalogResult.Errors, error => error.Code == "catalog.unavailable");
        Assert.Contains(authorityResult.Errors, error => error.Code == "authority.unavailable");
        Assert.Null(catalogResult.Graph);
        Assert.Null(authorityResult.Graph);
    }

    [Fact]
    public async Task ValidateDoesNotTreatAdvertisedDescriptorAsExecutable()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with { IsExecutable = false } : descriptor).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Evidence);
        Assert.Contains(result.Errors, error => error.Code == "node.descriptor.not-executable" && error.Element.Id == "infer");
    }

    [Fact]
    public async Task ValidateRequiresExactDescriptorKeyAndPortContract()
    {
        var candidate = Candidate();
        var missing = Descriptors(candidate).Where(descriptor => descriptor.Descriptor.TypeId != "provider-inference").ToArray();
        var incompatible = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            Ports = descriptor.Ports.Select(port => port.Id == "result"
                ? port with { AllowedValueKinds = GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Boolean]) }
                : port).ToArray()
        } : descriptor).ToArray();

        var missingResult = await Service(missing).ValidateAsync(candidate);
        var incompatibleResult = await Service(incompatible).ValidateAsync(candidate);

        Assert.Contains(missingResult.Errors, error => error.Code == "node.descriptor.not-advertised");
        Assert.Contains(incompatibleResult.Errors, error => error.Code == "node.port-contract.incompatible" && error.Element.Id == "infer.result");
    }

    [Fact]
    public async Task ValidateAdmitsCanonicalMultiKindPortsAndHashesTheExactAllowedSet()
    {
        var candidate = Candidate();
        var baseline = Descriptors(candidate);
        var widened = baseline.Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            Ports = descriptor.Ports.Select(port => port.Id == "result"
                ? port with { AllowedValueKinds = GovernedLoopValueKindSet.Create([GovernedLoopValueKind.Boolean, GovernedLoopValueKind.Text]) }
                : port).ToArray()
        } : descriptor).ToArray();

        var baselineResult = await Service(baseline).ValidateAsync(candidate);
        var widenedResult = await Service(widened).ValidateAsync(candidate);

        Assert.True(baselineResult.IsValid);
        Assert.True(widenedResult.IsValid);
        Assert.NotEqual(baselineResult.Evidence!.CatalogHash, widenedResult.Evidence!.CatalogHash);
    }

    [Fact]
    public async Task ValidateRejectsMissingAllowedKindSetBeforeEvidenceHashing()
    {
        var candidate = Candidate();
        var malformed = Descriptors(candidate);
        malformed[1] = malformed[1] with
        {
            Ports = malformed[1].Ports.Select(port => port.Id == "result"
                ? port with { AllowedValueKinds = null! }
                : port).ToArray()
        };

        var result = await Service(malformed).ValidateAsync(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Evidence);
        Assert.Contains(result.Errors, error => error.Code == "catalog.port-contract.invalid");
    }

    [Fact]
    public async Task ValidateRejectsIncompleteBranchOutcomes()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            AllowedControlOutcomes = [GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure],
            RequiredControlOutcomes = [GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure]
        } : descriptor).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "node.branch-outcome.missing" && error.Element.Id == "infer");
    }

    [Fact]
    public async Task ValidateRejectsUnsatisfiableAllPathJoin()
    {
        var join = new GovernedLoopNodeDefinition("join", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, "all-join", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var candidate = Candidate(
            nodes: Nodes().Append(join).ToArray(),
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-success-to-join", "infer", "join", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-failure-to-join", "infer", "join", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("join-to-exit", "join", "exit", GovernedLoopControlCondition.Always)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            AllowedControlOutcomes = [GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure],
            RequiredControlOutcomes = [GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure]
        } : descriptor).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "node.join.unsatisfiable" && error.Element.Id == "join");
    }

    [Theory]
    [InlineData(GovernedLoopControlCondition.Success)]
    [InlineData(GovernedLoopControlCondition.Failure)]
    [InlineData(GovernedLoopControlCondition.True)]
    [InlineData(GovernedLoopControlCondition.False)]
    [InlineData(GovernedLoopControlCondition.Approved)]
    [InlineData(GovernedLoopControlCondition.Rejected)]
    public async Task ValidateRejectsAllPathJoinOfTimeoutAndAnotherTerminalOutcome(GovernedLoopControlCondition otherOutcome)
    {
        var join = new GovernedLoopNodeDefinition("join", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, "all-join", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var candidate = Candidate(
            nodes: [.. Nodes(), join],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-other-to-join", "infer", "join", otherOutcome),
                new GovernedLoopControlEdgeDefinition("infer-timeout-to-join", "infer", "join", GovernedLoopControlCondition.Timeout),
                new GovernedLoopControlEdgeDefinition("join-to-exit", "join", "exit", GovernedLoopControlCondition.Always)
            ]);

        var result = await Service(Descriptors(candidate)).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "node.join.unsatisfiable" && error.Element.Id == "join");
    }

    [Fact]
    public async Task ValidateFindsUnsatisfiableJoinAcrossIntermediateBranchNodes()
    {
        var left = new GovernedLoopNodeDefinition("left", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "left-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var right = new GovernedLoopNodeDefinition("right", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "right-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var join = new GovernedLoopNodeDefinition("join", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, "all-join", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var candidate = Candidate(
            nodes: [.. Nodes(), left, right, join],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-left", "infer", "left", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-to-right", "infer", "right", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("left-to-join", "left", "join", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("right-to-join", "right", "join", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("join-to-exit", "join", "exit", GovernedLoopControlCondition.Always)
            ]);

        var result = await Service(Descriptors(candidate)).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "node.join.unsatisfiable" && error.Element.Id == "join");
    }

    [Fact]
    public async Task ValidateDoesNotUsePostJoinCycleToSatisfyFirstAllPathActivation()
    {
        var cycleBudgets = new Dictionary<string, string> { ["max-iterations"] = "2", ["max-milliseconds"] = "5000" };
        var left = new GovernedLoopNodeDefinition("left", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "left-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), cycleBudgets);
        var right = new GovernedLoopNodeDefinition("right", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "right-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var join = new GovernedLoopNodeDefinition("join", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, "all-join", 1), [], GovernedLoopAuthorityCeiling.Create([]), cycleBudgets);
        var candidate = Candidate(
            nodes: [.. Nodes(), left, right, join],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-success-to-left", "infer", "left", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-failure-to-right", "infer", "right", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("left-to-join", "left", "join", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("right-to-join", "right", "join", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("join-to-left", "join", "left", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("join-to-exit", "join", "exit", GovernedLoopControlCondition.Always)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId is "left-transform" or "all-join" ? EnableCycle(descriptor) : descriptor).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "node.join.unsatisfiable" && error.Element.Id == "join");
    }

    [Fact]
    public async Task ValidateRejectsAllPathJoinThatRequiresItsOwnFirstArrival()
    {
        var cycleBudgets = new Dictionary<string, string> { ["max-iterations"] = "2", ["max-milliseconds"] = "5000" };
        var join = new GovernedLoopNodeDefinition("join", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, "all-join", 1), [], GovernedLoopAuthorityCeiling.Create([]), cycleBudgets);
        var candidate = Candidate(
            nodes: [.. Nodes(), join],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-join", "infer", "join", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("join-to-join", "join", "join", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("join-to-exit", "join", "exit", GovernedLoopControlCondition.Success)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "all-join" ? EnableCycle(descriptor) : descriptor).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        var error = Assert.Single(result.Errors);
        Assert.Equal("node.join.unsatisfiable", error.Code);
        Assert.Equal("join", error.Element.Id);
    }

    [Fact]
    public async Task ValidateAcceptsAllPathJoinAfterLegitimateBoundedCycle()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Parameters = CycleParameters("2") };
        var left = new GovernedLoopNodeDefinition("left", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "left-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var right = new GovernedLoopNodeDefinition("right", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "right-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var join = new GovernedLoopNodeDefinition("join", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, "all-join", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var candidate = Candidate(
            nodes: [.. nodes, left, right, join],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-failure-to-infer", "infer", "infer", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("infer-success-to-left", "infer", "left", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-success-to-right", "infer", "right", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("left-to-join", "left", "join", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("right-to-join", "right", "join", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("join-success-to-exit", "join", "exit", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("join-failure-to-exit", "join", "exit", GovernedLoopControlCondition.Failure)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? EnableCycle(descriptor) : descriptor).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateIgnoresCurrentJoinOutcomesWhenCheckingFirstActivation()
    {
        var cycleBudgets = new Dictionary<string, string> { ["max-iterations"] = "2", ["max-milliseconds"] = "5000" };
        var left = new GovernedLoopNodeDefinition("left", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "left-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), cycleBudgets);
        var right = new GovernedLoopNodeDefinition("right", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "right-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), cycleBudgets);
        var join = new GovernedLoopNodeDefinition("join", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, "all-join", 1), [], GovernedLoopAuthorityCeiling.Create([]), cycleBudgets);
        var candidate = Candidate(
            nodes: [.. Nodes(), left, right, join],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-success-to-left", "infer", "left", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-success-to-right", "infer", "right", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("left-to-join", "left", "join", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("right-to-join", "right", "join", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("join-success-to-left", "join", "left", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("join-failure-to-right", "join", "right", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("join-always-to-exit", "join", "exit", GovernedLoopControlCondition.Always)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId is "left-transform" or "right-transform" or "all-join" ? EnableCycle(descriptor) : descriptor).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.Equal(
            ["graph.resources.activation-envelope", "node.cycle.internal-fan-out-unsupported"],
            result.Errors.Select(error => error.Code));
        Assert.Equal("join", result.Errors.Single(error => error.Code == "node.cycle.internal-fan-out-unsupported").Element.Id);
    }

    [Fact]
    public async Task ValidateRequiresExplicitBoundedCycleBudgets()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Parameters = new Dictionary<string, string> { ["instruction"] = "Answer safely.", ["max-iterations"] = "2", ["max-milliseconds"] = "5000" } };
        var edges = Edges().Append(new GovernedLoopControlEdgeDefinition("infer-loop", "infer", "infer", GovernedLoopControlCondition.Failure)).ToArray();
        var candidate = Candidate(nodes: nodes, edges: edges);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            AllowedControlOutcomes = [GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure],
            RequiredControlOutcomes = [GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure],
            AllowsCycle = true,
            CycleIterationBudgetParameterId = "max-iterations",
            CycleTimeBudgetMillisecondsParameterId = "max-milliseconds"
        } : descriptor).ToArray();

        var valid = await Service(descriptors).ValidateAsync(candidate);
        var overBudgetNodes = nodes.ToArray();
        overBudgetNodes[1] = overBudgetNodes[1] with { Parameters = new Dictionary<string, string> { ["instruction"] = "Answer safely.", ["max-iterations"] = (CustomLoopLimits.MaxGraphCycleIterations + 1).ToString(), ["max-milliseconds"] = "0" } };
        var invalid = await Service(descriptors).ValidateAsync(candidate with { Nodes = overBudgetNodes });

        Assert.True(valid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Code == "node.cycle.iteration-budget");
        Assert.Contains(invalid.Errors, error => error.Code == "node.cycle.time-budget");
    }

    [Fact]
    public async Task ValidateRejectsCurrentAuthorityWideningAndResourceLimits()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            ResourceBudget = new GovernedLoopNodeResourceBudget(2, 2, 2, 2)
        } : descriptor).ToArray();
        var authority = Authority(capabilityIds: [ModelInferenceCapability]) with { MaxAttempts = 1, MaxPayloadCharacters = 1, MaxEvidenceItems = 1, MaxResourceUnits = 1 };

        var result = await Service(descriptors, authority).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "authority.loop.widens-current-role");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.attempts");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.payload");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.evidence");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.units");
    }

    [Fact]
    public async Task ValidateBindsAuthorityEvidenceToTheOwningRole()
    {
        var candidate = Candidate();

        var result = await Service(Descriptors(candidate), Authority("different-role")).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "authority.role.mismatch");
    }

    [Fact]
    public async Task ValidateBindsTheFullOwningRoleRevisionAndContentHash()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate);
        var revisionSubstitution = AuthorityFromRevision(AuthorityGrantApplicationTestFixture.Role(
            capabilityIds: [ModelInferenceCapability, WorkspaceReadCapability],
            roleId: "researcher",
            revision: 2));
        var baseline = RoleRevision();
        var contentSubstitution = AuthorityFromRevision(ContextualRoleRevisionContentHash.Apply(baseline with
        {
            Purpose = "A substituted immutable role payload.",
        }));

        var revisionResult = await Service(descriptors, revisionSubstitution).ValidateAsync(candidate);
        var contentResult = await Service(descriptors, contentSubstitution).ValidateAsync(candidate);

        Assert.Contains(revisionResult.Errors, error => error.Code == "authority.role.mismatch");
        Assert.Contains(contentResult.Errors, error => error.Code == "authority.role.mismatch");
    }

    [Fact]
    public async Task ValidateEnforcesExactCanonicalParameterContracts()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with
        {
            Parameters = new Dictionary<string, string>
            {
                ["instruction"] = "abc",
                ["enabled"] = "true",
                ["retries"] = "5",
                ["target-id"] = "workspace-read",
                ["mode"] = "safe"
            }
        };
        var candidate = Candidate(nodes: nodes);
        var descriptors = WithExactParameterContracts(Descriptors(candidate));
        var valid = await Service(descriptors).ValidateAsync(candidate);
        nodes = nodes.ToArray();
        nodes[1] = nodes[1] with
        {
            Parameters = new Dictionary<string, string>
            {
                ["enabled"] = "True",
                ["retries"] = "05",
                ["target-id"] = "INVALID",
                ["mode"] = "unsafe",
                ["arbitrary"] = "ambient",
                ["instruction"] = "abcd"
            }
        };
        var invalid = await Service(descriptors).ValidateAsync(candidate with { Nodes = nodes });
        nodes[1] = nodes[1] with { Parameters = nodes[1].Parameters.Where(parameter => parameter.Key != "instruction").ToDictionary() };
        var missing = await Service(descriptors).ValidateAsync(candidate with { Nodes = nodes });

        Assert.True(valid.IsValid);
        Assert.Equal(5, invalid.Errors.Count(error => error.Code == "node.parameter.incompatible"));
        Assert.Contains(invalid.Errors, error => error.Code == "node.parameter.undeclared" && error.Element.Path.EndsWith("[arbitrary]", StringComparison.Ordinal));
        Assert.Contains(missing.Errors, error => error.Code == "node.parameter.required" && error.Element.Path.EndsWith("[instruction]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateEnforcesCanonicalFiniteNumberAndJsonPointerParameters()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with
        {
            Parameters = new Dictionary<string, string>
            {
                ["instruction"] = "Answer safely.",
                ["threshold"] = "15e-1",
                ["pointer"] = "/items/0"
            }
        };
        var candidate = Candidate(nodes: nodes);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            Parameters =
            [
                new GovernedLoopCatalogParameterContract("instruction", GovernedLoopParameterValueKind.Text, true, 1, CustomLoopLimits.MaxGraphParameterValueCharacters, null, null, []),
                new GovernedLoopCatalogParameterContract("threshold", GovernedLoopParameterValueKind.Number, true, 1, CustomLoopLimits.MaxGraphTypedValueNumberCharacters, null, null, []),
                new GovernedLoopCatalogParameterContract("pointer", GovernedLoopParameterValueKind.JsonPointer, true, 0, CustomLoopLimits.MaxGraphParameterValueCharacters, null, null, [])
            ]
        } : descriptor).ToArray();

        var valid = await Service(descriptors).ValidateAsync(candidate);
        nodes[1] = nodes[1] with
        {
            Parameters = new Dictionary<string, string>
            {
                ["instruction"] = "Answer safely.",
                ["threshold"] = "1.5",
                ["pointer"] = "/items/~2"
            }
        };
        var invalid = await Service(descriptors).ValidateAsync(candidate with { Nodes = nodes });
        var nullNumber = await Service(descriptors).ValidateAsync(candidate with
        {
            Nodes = nodes.Select(node => node.Id == "infer" ? node with
            {
                Parameters = new Dictionary<string, string>
                {
                    ["instruction"] = "Answer safely.",
                    ["threshold"] = "null",
                    ["pointer"] = string.Empty
                }
            } : node).ToArray()
        });
        var nonFiniteNumber = await Service(descriptors).ValidateAsync(candidate with
        {
            Nodes = nodes.Select(node => node.Id == "infer" ? node with
            {
                Parameters = new Dictionary<string, string>
                {
                    ["instruction"] = "Answer safely.",
                    ["threshold"] = "1e999",
                    ["pointer"] = string.Empty
                }
            } : node).ToArray()
        });

        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Errors.Select(error => $"{error.Code}: {error.Element.Path}")));
        Assert.Equal(2, invalid.Errors.Count(error => error.Code == "node.parameter.incompatible"));
        Assert.Contains(invalid.Errors, error => error.Element.Path.EndsWith("[threshold]", StringComparison.Ordinal));
        Assert.Contains(invalid.Errors, error => error.Element.Path.EndsWith("[pointer]", StringComparison.Ordinal));
        Assert.Contains(nullNumber.Errors, error => error.Code == "node.parameter.incompatible" && error.Element.Path.EndsWith("[threshold]", StringComparison.Ordinal));
        Assert.Contains(nonFiniteNumber.Errors, error => error.Code == "node.parameter.incompatible" && error.Element.Path.EndsWith("[threshold]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateRejectsMalformedParameterContractsAndHashesTheirSemantics()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate);
        var baseline = await Service(descriptors).ValidateAsync(candidate);
        var changed = descriptors.Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            Parameters = [new GovernedLoopCatalogParameterContract("instruction", GovernedLoopParameterValueKind.Text, true, 1, 4_000, null, null, [])]
        } : descriptor).ToArray();
        var changedResult = await Service(changed).ValidateAsync(candidate);
        var malformed = descriptors.Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            Parameters =
            [
                new GovernedLoopCatalogParameterContract("instruction", GovernedLoopParameterValueKind.Text, true, 1, 20, null, null, []),
                new GovernedLoopCatalogParameterContract("instruction", GovernedLoopParameterValueKind.Integer, true, 1, 20, null, null, []),
                new GovernedLoopCatalogParameterContract("mode", GovernedLoopParameterValueKind.Enumeration, false, 1, 20, null, null, []),
                new GovernedLoopCatalogParameterContract("unknown", GovernedLoopParameterValueKind.Unknown, false, 0, 20, null, null, [])
            ]
        } : descriptor).ToArray();
        var malformedResult = await Service(malformed).ValidateAsync(candidate);

        Assert.True(baseline.IsValid);
        Assert.True(changedResult.IsValid);
        Assert.NotEqual(baseline.Evidence!.CatalogHash, changedResult.Evidence!.CatalogHash);
        Assert.Contains(malformedResult.Errors, error => error.Code == "catalog.parameter-contract.invalid");
        Assert.Contains(malformedResult.Errors, error => error.Code == "catalog.parameter-contract.semantics");
    }

    [Fact]
    public async Task ValidateEnforcesParameterContractCountAtLimitAndLimitPlusOne()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate);
        var atLimitContracts = descriptors.Single(descriptor => descriptor.Descriptor.TypeId == "provider-inference").Parameters.Concat(Enumerable.Range(0, CustomLoopLimits.MaxGraphDescriptorParameters - 1).Select(index => new GovernedLoopCatalogParameterContract($"optional-{index:D2}", GovernedLoopParameterValueKind.Text, false, 0, 1, null, null, []))).ToArray();
        var atLimit = descriptors.Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with { Parameters = atLimitContracts } : descriptor).ToArray();
        var overLimit = atLimit.Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with { Parameters = [.. atLimitContracts, new GovernedLoopCatalogParameterContract("optional-extra", GovernedLoopParameterValueKind.Text, false, 0, 1, null, null, [])] } : descriptor).ToArray();

        var valid = await Service(atLimit).ValidateAsync(candidate);
        var invalid = await Service(overLimit).ValidateAsync(candidate);

        Assert.True(valid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Code == "catalog.parameter-contract.count");
    }

    [Fact]
    public async Task ValidateMultipliesCyclicResourcesAtEveryLimitAndLimitPlusOne()
    {
        var candidate = CyclicCandidate("5");
        var atLimit = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? EnableCycle(descriptor) with
        {
            ResourceBudget = new GovernedLoopNodeResourceBudget(13, 51_200, 6, 20_000)
        } : descriptor).ToArray();
        var overLimit = atLimit.Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            ResourceBudget = new GovernedLoopNodeResourceBudget(14, 51_201, 7, 20_001)
        } : descriptor).ToArray();

        var valid = await Service(atLimit, Authority() with { MaxPayloadCharacters = CustomLoopLimits.MaxGraphNodePayloadCharacters, MaxResourceUnits = CustomLoopLimits.MaxGraphNodeResourceUnits }).ValidateAsync(candidate);
        var invalid = await Service(overLimit, Authority() with { MaxPayloadCharacters = CustomLoopLimits.MaxGraphNodePayloadCharacters, MaxResourceUnits = CustomLoopLimits.MaxGraphNodeResourceUnits }).ValidateAsync(candidate);

        Assert.True(valid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Code == "graph.resources.attempts");
        Assert.Contains(invalid.Errors, error => error.Code == "graph.resources.payload");
        Assert.Contains(invalid.Errors, error => error.Code == "graph.resources.evidence");
        Assert.Contains(invalid.Errors, error => error.Code == "graph.resources.units");
    }

    [Fact]
    public async Task ValidateFailsClosedWhenCycleResourceArithmeticWouldOverflow()
    {
        var candidate = CyclicCandidate(long.MaxValue.ToString());
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId switch
        {
            "provider-inference" => EnableCycle(descriptor) with { ResourceBudget = new GovernedLoopNodeResourceBudget(2, 2, 2, 2) },
            "manual-trigger" => descriptor with { ResourceBudget = new GovernedLoopNodeResourceBudget(1, 1, 1, 1) },
            _ => descriptor
        }).ToArray();

        var result = await Service(descriptors, Authority() with { MaxPayloadCharacters = CustomLoopLimits.MaxGraphNodePayloadCharacters, MaxResourceUnits = CustomLoopLimits.MaxGraphNodeResourceUnits }).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "node.parameter.incompatible");
        Assert.Contains(result.Errors, error => error.Code == "node.cycle.iteration-budget");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.activation-envelope");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.attempts");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.payload");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.evidence");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.units");
    }

    [Fact]
    public async Task ValidateAcceptsSingleSuccessorNestedCycleAtLimitAndRejectsLimitPlusOne()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Parameters = CycleParameters("2") };
        var nested = new GovernedLoopNodeDefinition("nested", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "nested-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), CycleParameters("3"));
        var candidate = Candidate(
            nodes: [.. nodes, nested],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-nested", "infer", "nested", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("nested-to-infer", "nested", "infer", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("nested-to-exit", "nested", "exit", GovernedLoopControlCondition.Success)
            ]);
        var atLimit = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId switch
        {
            "provider-inference" or "nested-transform" => EnableCycle(descriptor) with { ResourceBudget = new GovernedLoopNodeResourceBudget(5, 0, 0, 0) },
            _ => descriptor
        }).ToArray();
        var overLimit = atLimit.Select(descriptor => descriptor.Descriptor.TypeId == "nested-transform" ? descriptor with { ResourceBudget = new GovernedLoopNodeResourceBudget(6, 0, 0, 0) } : descriptor).ToArray();

        var valid = await Service(atLimit, Authority() with { MaxAttempts = 60 }).ValidateAsync(candidate);
        var invalid = await Service(overLimit, Authority() with { MaxAttempts = 65 }).ValidateAsync(candidate);

        Assert.True(valid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Code == "graph.resources.attempts");
    }

    [Fact]
    public async Task ValidateRejectsParallelInternalCycleEdgesWithZeroBudgetsDeterministically()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Parameters = CycleParameters("1") };
        var nested = new GovernedLoopNodeDefinition("nested", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "nested-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), CycleParameters("1"));
        var candidate = Candidate(
            nodes: [.. nodes, nested],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-nested-success", "infer", "nested", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-to-nested-failure", "infer", "nested", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("nested-to-infer", "nested", "infer", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("nested-to-exit", "nested", "exit", GovernedLoopControlCondition.Success)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId is "provider-inference" or "nested-transform" ? EnableCycle(descriptor) : descriptor).ToArray();

        var expected = await Service(descriptors).ValidateAsync(candidate);
        var permuted = await Service(descriptors.Reverse().ToArray()).ValidateAsync(candidate with { ControlEdges = candidate.ControlEdges!.Reverse().ToArray() });

        Assert.Equal(expected.Evidence, permuted.Evidence);
        Assert.Equal(expected.Errors, permuted.Errors);
        Assert.Contains(expected.Errors, error => error.Code == "node.cycle.internal-fan-out-unsupported" && error.Element.Kind == GovernedLoopGraphElementKind.Node && error.Element.Id == "infer" && error.Element.Path == "graph.nodes[infer]");
        Assert.Contains(expected.Errors, error => error.Code == "graph.resources.activation-envelope");
        Assert.DoesNotContain(expected.Errors, error => error.Code.StartsWith("graph.resources.", StringComparison.Ordinal) && error.Code != "graph.resources.activation-envelope");
    }

    [Fact]
    public async Task ValidateRejectsInternalCycleBranchAndReconvergence()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Parameters = CycleParameters("1") };
        var left = new GovernedLoopNodeDefinition("left", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "left-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), CycleParameters("1"));
        var right = new GovernedLoopNodeDefinition("right", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "right-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), CycleParameters("1"));
        var candidate = Candidate(
            nodes: [.. nodes, left, right],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-left", "infer", "left", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-to-right", "infer", "right", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("left-to-infer", "left", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("right-to-infer", "right", "infer", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("right-to-exit", "right", "exit", GovernedLoopControlCondition.Success)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId is "provider-inference" or "left-transform" or "right-transform" ? EnableCycle(descriptor) : descriptor).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "node.cycle.internal-fan-out-unsupported" && error.Element.Id == "infer");
        Assert.Contains(result.Errors, error => error.Code == "graph.resources.activation-envelope");
    }

    [Fact]
    public async Task ValidateFailsClosedWhenNestedCycleMultiplierSaturatesWithZeroBudgets()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Parameters = CycleParameters(CustomLoopLimits.MaxGraphCycleIterations.ToString()) };
        var cycleNodes = Enumerable.Range(0, 4).Select(index => new GovernedLoopNodeDefinition($"cycle-{index}", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, $"cycle-transform-{index}", 1), [], GovernedLoopAuthorityCeiling.Create([]), CycleParameters(CustomLoopLimits.MaxGraphCycleIterations.ToString()))).ToArray();
        var candidate = Candidate(
            nodes: [.. nodes, .. cycleNodes],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-cycle-0", "infer", "cycle-0", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("cycle-0-to-cycle-1", "cycle-0", "cycle-1", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("cycle-1-to-cycle-2", "cycle-1", "cycle-2", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("cycle-2-to-cycle-3", "cycle-2", "cycle-3", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("cycle-3-to-infer", "cycle-3", "infer", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("cycle-3-to-exit", "cycle-3", "exit", GovernedLoopControlCondition.Success)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId is "provider-inference" or "cycle-transform-0" or "cycle-transform-1" or "cycle-transform-2" or "cycle-transform-3" ? EnableCycle(descriptor) : descriptor).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "graph.resources.activation-envelope");
        Assert.DoesNotContain(result.Errors, error => error.Code.StartsWith("graph.resources.", StringComparison.Ordinal) && error.Code != "graph.resources.activation-envelope");
    }

    [Fact]
    public async Task ValidatePricesAcyclicReconvergencePerIncomingActivation()
    {
        var nodes = Nodes();
        nodes[2] = nodes[2] with { Ports = nodes[2].Ports.Select(port => port.Id == "result" ? port with { Required = false } : port).ToArray() };
        var extra = new GovernedLoopNodeDefinition("extra", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "extra-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var candidate = Candidate(
            nodes: [.. nodes, extra],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("trigger-to-extra", "trigger", "extra", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("extra-to-exit", "extra", "exit", GovernedLoopControlCondition.Success)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId == "success-exit" ? descriptor with { ResourceBudget = new GovernedLoopNodeResourceBudget(10, 0, 0, 0) } : descriptor).ToArray();

        var valid = await Service(descriptors, Authority() with { MaxAttempts = 20 }).ValidateAsync(candidate);
        var invalid = await Service(descriptors.Reverse().ToArray(), Authority() with { MaxAttempts = 19 }).ValidateAsync(candidate with { ControlEdges = candidate.ControlEdges!.Reverse().ToArray() });

        Assert.True(valid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Code == "graph.resources.attempts");
    }

    [Fact]
    public async Task ValidatePropagatesSeparateSccEntriesWithoutDoubleCountingDagNodes()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Parameters = CycleParameters("2") };
        nodes[2] = nodes[2] with { Ports = nodes[2].Ports.Select(port => port.Id == "result" ? port with { Required = false } : port).ToArray() };
        var second = new GovernedLoopNodeDefinition("second", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "second-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), CycleParameters("2"));
        var candidate = Candidate(
            nodes: [.. nodes, second],
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("trigger-to-second", "trigger", "second", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-loop", "infer", "infer", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("infer-to-second", "infer", "second", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("second-loop", "second", "second", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("second-to-exit", "second", "exit", GovernedLoopControlCondition.Success)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId switch
        {
            "manual-trigger" => descriptor with { ResourceBudget = new GovernedLoopNodeResourceBudget(5, 0, 0, 0) },
            "provider-inference" or "second-transform" => EnableCycle(descriptor) with { ResourceBudget = new GovernedLoopNodeResourceBudget(5, 0, 0, 0) },
            _ => descriptor
        }).ToArray();

        var valid = await Service(descriptors, Authority() with { MaxAttempts = 45 }).ValidateAsync(candidate);
        var invalid = await Service(descriptors.Reverse().ToArray(), Authority() with { MaxAttempts = 44 }).ValidateAsync(candidate with { ControlEdges = candidate.ControlEdges!.Reverse().ToArray() });

        Assert.True(valid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Code == "graph.resources.attempts");
    }

    [Fact]
    public async Task ValidateRejectsMalformedSnapshotsAndHonorsCancellation()
    {
        var candidate = Candidate();
        var malformedCatalog = new GovernedLoopNodeCatalogSnapshot(true, "INVALID", Descriptors(candidate));
        var malformedResult = await new GovernedLoopGraphValidationService(new FixedCatalog(malformedCatalog), new FixedAuthority(Authority())).ValidateAsync(candidate);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Contains(malformedResult.Errors, error => error.Code == "catalog.snapshot.invalid");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GovernedLoopGraphValidationService(new CancelingCatalog(), new FixedAuthority(Authority())).ValidateAsync(candidate, cancellation.Token));
    }

    [Fact]
    public async Task ValidateRejectsMalformedCatalogDescriptorContracts()
    {
        var candidate = Candidate();
        var valid = Descriptors(candidate);
        var malformed = valid[1] with
        {
            JoinPolicy = GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges = 2,
            AllowedControlOutcomes = [GovernedLoopControlCondition.Unknown, GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Success],
            RequiredControlOutcomes = [GovernedLoopControlCondition.Failure],
            CycleIterationBudgetParameterId = "iterations",
            Ports = [.. valid[1].Ports, valid[1].Ports[0]],
            RequiredCapabilityIds = ["INVALID"],
            ResourceBudget = new GovernedLoopNodeResourceBudget(-1, 0, 0, 0)
        };
        GovernedLoopNodeCatalogDescriptor nullDescriptor = null!;
        var snapshot = new GovernedLoopNodeCatalogSnapshot(true, "catalog-1", [.. valid, malformed, valid[0], nullDescriptor]);

        var result = await new GovernedLoopGraphValidationService(new FixedCatalog(snapshot), new FixedAuthority(Authority())).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "catalog.descriptor.invalid");
        Assert.Contains(result.Errors, error => error.Code == "catalog.descriptor.duplicate");
        Assert.Contains(result.Errors, error => error.Code == "catalog.join-contract.invalid");
        Assert.Contains(result.Errors, error => error.Code == "catalog.control-outcomes.invalid");
        Assert.Contains(result.Errors, error => error.Code == "catalog.cycle-budget-contract.invalid");
        Assert.Contains(result.Errors, error => error.Code == "catalog.port-contract.invalid");
        Assert.Contains(result.Errors, error => error.Code == "catalog.capabilities.invalid");
        Assert.Contains(result.Errors, error => error.Code == "catalog.resource-budget.invalid");
    }

    [Fact]
    public async Task ValidateCapsCatalogErrorsAfterCanonicalSortingAcrossProviderPermutations()
    {
        var candidate = Candidate();
        var template = Descriptors(candidate)[1];
        var malformed = Enumerable.Range(0, CustomLoopLimits.MaxGraphNodes).Select(index => template with
        {
            Descriptor = new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, $"malformed-{index:D3}", 1),
            JoinPolicy = GovernedLoopJoinPolicy.None,
            MinimumIncomingControlEdges = 2,
            AllowedControlOutcomes = [GovernedLoopControlCondition.Unknown],
            RequiredControlOutcomes = [GovernedLoopControlCondition.Failure],
            CycleIterationBudgetParameterId = "iterations",
            Ports = [new GovernedLoopCatalogPortContract("INVALID", GovernedLoopPortDirection.Unknown, GovernedLoopBindingKind.Unknown, null!, true)],
            RequiredCapabilityIds = ["INVALID"],
            ResourceBudget = new GovernedLoopNodeResourceBudget(-1, -1, -1, -1)
        }).ToArray();

        var forward = await Service(malformed).ValidateAsync(candidate);
        var reverse = await Service(malformed.Reverse().ToArray()).ValidateAsync(candidate);

        Assert.Equal(CustomLoopLimits.MaxGraphValidationErrors, forward.Errors.Count);
        Assert.Equal(forward.Errors, reverse.Errors);
        Assert.Equal(forward.Errors.OrderBy(error => error.Element.Path, StringComparer.Ordinal).ThenBy(error => error.Code, StringComparer.Ordinal).ThenBy(error => error.Element.Id, StringComparer.Ordinal), forward.Errors);
    }

    [Fact]
    public async Task ValidateRejectsOversizedCatalogPortsWithoutEnumeratingOrHashingThem()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate);
        descriptors[1] = descriptors[1] with { Ports = new ThrowingEnumerationReadOnlyList<GovernedLoopCatalogPortContract>(CustomLoopLimits.MaxGraphPortsPerNode + 1) };

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.Null(result.Evidence);
        Assert.Contains(result.Errors, error => error.Code == "catalog.port-contract.count" && error.Element.Id == "provider-inference");
    }

    [Fact]
    public async Task ValidateRejectsOversizedSiblingProviderCollectionsWithoutEnumeratingThem()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate);
        var outcomeMaximum = Enum.GetValues<GovernedLoopControlCondition>().Count(value => value != GovernedLoopControlCondition.Unknown);
        var outcomes = descriptors.ToArray();
        outcomes[1] = outcomes[1] with { AllowedControlOutcomes = new ThrowingEnumerationReadOnlyList<GovernedLoopControlCondition>(outcomeMaximum + 1) };
        var parameters = descriptors.ToArray();
        parameters[1] = parameters[1] with { Parameters = new ThrowingEnumerationReadOnlyList<GovernedLoopCatalogParameterContract>(CustomLoopLimits.MaxGraphDescriptorParameters + 1) };
        var capabilities = descriptors.ToArray();
        capabilities[1] = capabilities[1] with { RequiredCapabilityIds = new ThrowingEnumerationReadOnlyList<string>(CustomLoopLimits.MaxGraphAuthorityCapabilities + 1) };
        var authority = Authority() with { CapabilityIds = new ThrowingEnumerationReadOnlyList<string>(CustomLoopLimits.MaxGraphAuthorityCapabilities + 1) };

        var outcomeResult = await Service(outcomes).ValidateAsync(candidate);
        var parameterResult = await Service(parameters).ValidateAsync(candidate);
        var capabilityResult = await Service(capabilities).ValidateAsync(candidate);
        var authorityResult = await Service(descriptors, authority).ValidateAsync(candidate);

        Assert.Contains(outcomeResult.Errors, error => error.Code == "catalog.control-outcomes.count");
        Assert.Contains(parameterResult.Errors, error => error.Code == "catalog.parameter-contract.count");
        Assert.Contains(capabilityResult.Errors, error => error.Code == "catalog.capabilities.count");
        Assert.Contains(authorityResult.Errors, error => error.Code == "authority.capabilities.count");
        Assert.All(new[] { outcomeResult, parameterResult, capabilityResult, authorityResult }, result => Assert.Null(result.Evidence));
    }

    [Fact]
    public async Task ValidateRejectsDescriptorAuthorityEntryTerminalAndOutcomeConflicts()
    {
        var extra = new GovernedLoopNodeDefinition("extra", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "extra-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var candidate = Candidate(
            nodes: Nodes().Append(extra).ToArray(),
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("trigger-to-extra", "trigger", "extra", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("extra-to-infer", "extra", "infer", GovernedLoopControlCondition.Success)
            ]);
        var descriptors = Descriptors(candidate).Select(descriptor => descriptor.Descriptor.TypeId switch
        {
            "manual-trigger" => descriptor with { IsLegalEntry = false },
            "success-exit" => descriptor with { IsLegalTerminal = false },
            "provider-inference" => descriptor with
            {
                IsAdvertised = false,
                AllowedControlOutcomes = [GovernedLoopControlCondition.Failure],
                RequiredControlOutcomes = [],
                Ports = descriptor.Ports.Where(port => port.Id != "result").ToArray(),
                RequiredCapabilityIds = [WorkspaceReadCapability]
            },
            _ => descriptor
        }).ToArray();

        var result = await Service(descriptors).ValidateAsync(candidate);

        Assert.Contains(result.Errors, error => error.Code == "node.entry.illegal");
        Assert.Contains(result.Errors, error => error.Code == "node.terminal.contract");
        Assert.Contains(result.Errors, error => error.Code == "node.descriptor.not-advertised");
        Assert.Contains(result.Errors, error => error.Code == "node.port-contract.mismatch");
        Assert.Contains(result.Errors, error => error.Code == "node.authority.missing-capability");
        Assert.Contains(result.Errors, error => error.Code == "edge.outcome.not-allowed");
    }

    [Fact]
    public async Task ValidateRejectsInsufficientJoinAndUnbudgetedCycle()
    {
        var join = new GovernedLoopNodeDefinition("join", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Join, "all-join", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var joinCandidate = Candidate(
            nodes: Nodes().Append(join).ToArray(),
            edges:
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-join", "infer", "join", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("join-to-exit", "join", "exit", GovernedLoopControlCondition.Always)
            ]);
        var joinResult = await Service(Descriptors(joinCandidate)).ValidateAsync(joinCandidate);
        var cycleCandidate = Candidate(edges: Edges().Append(new GovernedLoopControlEdgeDefinition("infer-loop", "infer", "infer", GovernedLoopControlCondition.Failure)).ToArray());
        var cycleResult = await Service(Descriptors(cycleCandidate)).ValidateAsync(cycleCandidate);

        Assert.Contains(joinResult.Errors, error => error.Code == "node.join.incoming-insufficient");
        Assert.Contains(cycleResult.Errors, error => error.Code == "node.cycle.not-allowed");
    }

    [Fact]
    public async Task ValidateRejectsMalformedAuthorityAndProviderFailures()
    {
        var candidate = Candidate();
        var descriptors = Descriptors(candidate);
        var malformedAuthority = Authority() with { OwningRole = RolePin("different-role"), CapabilityIds = ["INVALID"], MaxAttempts = -1 };
        var malformedResult = await Service(descriptors, malformedAuthority).ValidateAsync(candidate);
        var catalogFailure = await new GovernedLoopGraphValidationService(new ThrowingCatalog(), new FixedAuthority(Authority())).ValidateAsync(candidate);
        var authorityFailure = await new GovernedLoopGraphValidationService(new FixedCatalog(new GovernedLoopNodeCatalogSnapshot(true, "catalog-1", descriptors)), new ThrowingAuthority()).ValidateAsync(candidate);

        Assert.Contains(malformedResult.Errors, error => error.Code == "authority.snapshot.invalid");
        Assert.Contains(malformedResult.Errors, error => error.Code == "authority.resource-limits.invalid");
        Assert.Contains(catalogFailure.Errors, error => error.Code == "catalog.unavailable");
        Assert.Contains(authorityFailure.Errors, error => error.Code == "authority.unavailable");
    }

    private static GovernedLoopGraphValidationService Service(IReadOnlyList<GovernedLoopNodeCatalogDescriptor> descriptors, GovernedLoopAuthoritySnapshot? authority = null)
    {
        return new GovernedLoopGraphValidationService(new FixedCatalog(new GovernedLoopNodeCatalogSnapshot(true, "catalog-1", descriptors)), new FixedAuthority(authority ?? Authority()));
    }

    private static T[] Rotate<T>(IReadOnlyList<T> values, int offset)
    {
        var start = offset % values.Count;
        return values.Skip(start).Concat(values.Take(start)).ToArray();
    }

    private static GovernedLoopNodeCatalogDescriptor[] WithExactParameterContracts(IEnumerable<GovernedLoopNodeCatalogDescriptor> descriptors)
    {
        return descriptors.Select(descriptor => descriptor.Descriptor.TypeId == "provider-inference" ? descriptor with
        {
            Parameters =
            [
                new GovernedLoopCatalogParameterContract("instruction", GovernedLoopParameterValueKind.Text, true, 1, 3, null, null, []),
                new GovernedLoopCatalogParameterContract("enabled", GovernedLoopParameterValueKind.Boolean, true, 4, 5, null, null, []),
                IntegerParameter("retries", 1, 10),
                new GovernedLoopCatalogParameterContract("target-id", GovernedLoopParameterValueKind.Identifier, true, 1, CustomLoopLimits.MaxArtifactIdCharacters, null, null, []),
                new GovernedLoopCatalogParameterContract("mode", GovernedLoopParameterValueKind.Enumeration, true, 1, 20, null, null, ["safe", "strict"])
            ]
        } : descriptor).ToArray();
    }

    private static GovernedLoopGraphCandidate CyclicCandidate(string iterations)
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Parameters = CycleParameters(iterations) };
        return Candidate(nodes: nodes, edges: Edges().Append(new GovernedLoopControlEdgeDefinition("infer-loop", "infer", "infer", GovernedLoopControlCondition.Failure)).ToArray());
    }

    private static Dictionary<string, string> CycleParameters(string iterations)
    {
        return new Dictionary<string, string> { ["instruction"] = "Answer safely.", ["max-iterations"] = iterations, ["max-milliseconds"] = "5000" };
    }

    private static GovernedLoopNodeCatalogDescriptor EnableCycle(GovernedLoopNodeCatalogDescriptor descriptor)
    {
        return descriptor with { AllowsCycle = true, CycleIterationBudgetParameterId = "max-iterations", CycleTimeBudgetMillisecondsParameterId = "max-milliseconds" };
    }

    private static GovernedLoopAuthoritySnapshot Authority(
        string roleId = "researcher",
        IReadOnlyList<string>? capabilityIds = null)
    {
        var revision = RoleRevision(roleId, capabilityIds);
        return AuthorityFromRevision(revision);
    }

    private static GovernedLoopAuthoritySnapshot AuthorityFromRevision(ContextualRoleRevision revision)
    {
        var pin = new ContextualRoleRevisionPin(revision.Identity, revision.ContentHash);
        return new GovernedLoopAuthoritySnapshot(
            true,
            AuthorityGrantApplicationTestFixture.Hash64('e'),
            pin,
            revision,
            AuthorityGrantApplicationTestFixture.RoleLifecycle(revision),
            AuthorityGrantApplicationTestFixture.WorkspaceId,
            ContextualRoleInstructionSourceProbeStatus.Ready,
            revision.PolicyMaxima.CapabilityIds,
            CustomLoopLimits.MaxGraphNodeAttempts,
            100_000,
            CustomLoopLimits.MaxGraphNodeEvidenceItems,
            100);
    }

    private static ContextualRoleRevision RoleRevision(
        string roleId = "researcher",
        IReadOnlyList<string>? capabilityIds = null)
        => AuthorityGrantApplicationTestFixture.Role(
            capabilityIds: capabilityIds ?? [ModelInferenceCapability, WorkspaceReadCapability],
            roleId: roleId);

    private static ContextualRoleRevisionPin RolePin(string roleId = "researcher")
    {
        var revision = RoleRevision(roleId);
        return new ContextualRoleRevisionPin(revision.Identity, revision.ContentHash);
    }

    private static GovernedLoopNodeCatalogDescriptor[] Descriptors(GovernedLoopGraphCandidate candidate)
    {
        var schemas = candidate.ValueSchemas!.Where(schema => schema is not null).ToDictionary(schema => schema!.Id, schema => schema!.Kind, StringComparer.Ordinal);
        var terminalIds = candidate.TerminalNodeIds!.Where(value => value is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        return candidate.Nodes!.Where(node => node is not null).Cast<GovernedLoopNodeDefinition>().Select(node =>
        {
            var outcomes = candidate.ControlEdges!.Where(edge => edge is not null && edge.FromNodeId == node.Id).Select(edge => edge!.Condition).Distinct().Order().ToArray();
            var join = node.Descriptor.Kind == GovernedLoopNodeKind.Join;
            return new GovernedLoopNodeCatalogDescriptor(
                node.Descriptor,
                true,
                true,
                node.Descriptor.Kind == GovernedLoopNodeKind.Trigger,
                terminalIds.Contains(node.Id),
                outcomes,
                outcomes,
                join ? GovernedLoopJoinPolicy.All : GovernedLoopJoinPolicy.None,
                join ? 2 : 0,
                false,
                null,
                null,
                node.Ports.Select(port => new GovernedLoopCatalogPortContract(port.Id, port.Direction, port.BindingKind, GovernedLoopValueKindSet.Create([schemas[port.ValueSchemaId]]), port.Required)).ToArray(),
                ParameterContracts(node),
                node.AuthorityCeiling.CapabilityIds,
                new GovernedLoopNodeResourceBudget(0, 0, 0, 0));
        }).ToArray();
    }

    private static GovernedLoopCatalogParameterContract[] ParameterContracts(GovernedLoopNodeDefinition node)
    {
        return node.Parameters.Select(parameter => parameter.Key switch
        {
            "max-iterations" => IntegerParameter(parameter.Key, 1, CustomLoopLimits.MaxGraphCycleIterations),
            "max-milliseconds" => IntegerParameter(parameter.Key, 1, CustomLoopLimits.MaxGraphCycleMilliseconds),
            _ => new GovernedLoopCatalogParameterContract(parameter.Key, GovernedLoopParameterValueKind.Text, true, 1, CustomLoopLimits.MaxGraphParameterValueCharacters, null, null, [])
        }).ToArray();
    }

    private static GovernedLoopCatalogParameterContract IntegerParameter(string id, long minimum, long maximum)
    {
        return new GovernedLoopCatalogParameterContract(id, GovernedLoopParameterValueKind.Integer, true, 1, 20, minimum, maximum, []);
    }

    private static GovernedLoopGraphCandidate Candidate(IReadOnlyList<GovernedLoopNodeDefinition?>? nodes = null, IReadOnlyList<GovernedLoopControlEdgeDefinition?>? edges = null)
    {
        return new GovernedLoopGraphCandidate(1, "research-loop", "revision-1", "Research one question safely.", RolePin(), "trigger", ["exit"], GovernedLoopAuthorityCeiling.Create([ModelInferenceCapability, WorkspaceReadCapability]), Schemas(), nodes ?? Nodes(), edges ?? Edges(), Bindings(), Output(), Display());
    }

    private static GovernedLoopValueSchemaDefinition[] Schemas() => [new("text", GovernedLoopValueKind.Text, false)];

    private static GovernedLoopNodeDefinition[] Nodes()
    {
        return
        [
            new GovernedLoopNodeDefinition("trigger", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1), [Output("request", GovernedLoopBindingKind.Data), Output("invocation-context", GovernedLoopBindingKind.Context)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition("infer", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1), [Input("request", GovernedLoopBindingKind.Data), Input("invocation-context", GovernedLoopBindingKind.Context), Output("result", GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create([ModelInferenceCapability]), new Dictionary<string, string> { ["instruction"] = "Answer safely." }),
            new GovernedLoopNodeDefinition("exit", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1), [Input("result", GovernedLoopBindingKind.Data), Output("published-result", GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>())
        ];
    }

    private static GovernedLoopControlEdgeDefinition[] Edges()
    {
        return [new("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always), new("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success)];
    }

    private static GovernedLoopBindingDefinition[] Bindings()
    {
        return
        [
            new("request-binding", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
            new("context-binding", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
            new("result-binding", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result")
        ];
    }

    private static GovernedLoopOutputContract Output() => new("Return the result.", [new("result", "text", "exit", "published-result", true)]);

    private static GovernedLoopDisplayMetadata Display() => new("Research loop", "Display only.", [new("trigger", "Trigger", "Start."), new("infer", "Inference", "Answer."), new("exit", "Exit", "Finish.")]);

    private static GovernedLoopPortDefinition Input(string id, GovernedLoopBindingKind kind) => new(id, GovernedLoopPortDirection.Input, kind, "text", true);

    private static GovernedLoopPortDefinition Output(string id, GovernedLoopBindingKind kind) => new(id, GovernedLoopPortDirection.Output, kind, "text", true);

    private sealed class FixedCatalog(GovernedLoopNodeCatalogSnapshot snapshot) : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class FixedAuthority(GovernedLoopAuthoritySnapshot snapshot) : IGovernedLoopAuthoritySnapshotProvider
    {
        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(ContextualRoleRevisionPin? owningRole, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class CancelingCatalog : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromCanceled<GovernedLoopNodeCatalogSnapshot>(cancellationToken);
    }

    private sealed class ThrowingCatalog : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromException<GovernedLoopNodeCatalogSnapshot>(new IOException("Unavailable."));
    }

    private sealed class ThrowingAuthority : IGovernedLoopAuthoritySnapshotProvider
    {
        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(ContextualRoleRevisionPin? owningRole, CancellationToken cancellationToken = default) => Task.FromException<GovernedLoopAuthoritySnapshot>(new IOException("Unavailable."));
    }
}
