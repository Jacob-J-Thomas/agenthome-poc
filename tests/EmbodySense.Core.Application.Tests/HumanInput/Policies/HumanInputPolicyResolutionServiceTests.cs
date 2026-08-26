using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.HumanInput.Policies;

public sealed class HumanInputPolicyResolutionServiceTests
{
    [Fact]
    public async Task Exact_server_scoped_policies_resolve_once_under_trusted_time_without_defaults()
    {
        var source = Source();
        var result = await new HumanInputPolicyResolutionService(source, new FixedTimeProvider(_at)).ResolveAsync(Request());

        Assert.Equal(HumanInputPolicyResolutionStatus.Resolved, result.Status);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(_at.AddHours(1), result.Snapshot!.ExpiresAtUtc);
        Assert.Equal("timeout-one@revision-one", result.Snapshot.TimeoutPolicy.Reference.ToString());
        Assert.Equal("failure-one@revision-one", result.Snapshot.FailurePolicy.Reference.ToString());
    }

    [Fact]
    public async Task Missing_divergent_wrong_kind_scope_and_unavailable_sources_fail_closed()
    {
        var missing = await new HumanInputPolicyResolutionService(new HumanInputPolicyResolutionTestSource(), new FixedTimeProvider(_at)).ResolveAsync(Request());
        var divergentSource = Source();
        divergentSource.Results[new HumanInputPolicyReference("timeout-one", "revision-one")] = new(HumanInputPolicySourceReadStatus.Ready, HumanInputPolicyArtifactHash.Apply(Timeout() with { RevisionId = "revision-two" }), 1);
        var divergent = await new HumanInputPolicyResolutionService(divergentSource, new FixedTimeProvider(_at)).ResolveAsync(Request());
        var wrongKindSource = Source();
        wrongKindSource.Results[new HumanInputPolicyReference("timeout-one", "revision-one")] = new(HumanInputPolicySourceReadStatus.Ready, HumanInputPolicyArtifactHash.Apply(Timeout() with { Kind = HumanInputPolicyKind.DeadlineDisposition, ResponseWindowMilliseconds = null, TerminalDisposition = HumanInputTerminalDisposition.Expired }), 1);
        var wrongKind = await new HumanInputPolicyResolutionService(wrongKindSource, new FixedTimeProvider(_at)).ResolveAsync(Request());
        var scopeSource = Source();
        scopeSource.Results[new HumanInputPolicyReference("failure-one", "revision-one")] = new(HumanInputPolicySourceReadStatus.Ready, HumanInputPolicyArtifactHash.Apply(Failure() with { GraphId = "graph-two" }), 1);
        var scope = await new HumanInputPolicyResolutionService(scopeSource, new FixedTimeProvider(_at)).ResolveAsync(Request());
        var unavailableSource = Source();
        unavailableSource.Results[new HumanInputPolicyReference("failure-one", "revision-one")] = new(HumanInputPolicySourceReadStatus.Unavailable, null, 0);
        var unavailable = await new HumanInputPolicyResolutionService(unavailableSource, new FixedTimeProvider(_at)).ResolveAsync(Request());

        Assert.Equal(HumanInputPolicyResolutionStatus.NotFound, missing.Status);
        Assert.Equal(HumanInputPolicyResolutionStatus.Divergent, divergent.Status);
        Assert.Equal(HumanInputPolicyResolutionStatus.WrongKind, wrongKind.Status);
        Assert.Equal(HumanInputPolicyResolutionStatus.ScopeMismatch, scope.Status);
        Assert.Equal(HumanInputPolicyResolutionStatus.Unavailable, unavailable.Status);
    }

    [Fact]
    public async Task Malformed_unversioned_configuration_and_non_utc_trusted_time_fail_closed()
    {
        var malformed = Request() with { Configuration = Configuration("timeout-one", "failure-one@revision-one") };
        var defaulted = Request() with { Configuration = Configuration("default@revision-one", "failure-one@revision-one") };
        var malformedResult = await new HumanInputPolicyResolutionService(Source(), new FixedTimeProvider(_at)).ResolveAsync(malformed);
        var defaultedResult = await new HumanInputPolicyResolutionService(Source(), new FixedTimeProvider(_at)).ResolveAsync(defaulted);
        var clockResult = await new HumanInputPolicyResolutionService(Source(), new FixedTimeProvider(new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.FromHours(-5)))).ResolveAsync(Request());

        Assert.Equal(HumanInputPolicyResolutionStatus.Invalid, malformedResult.Status);
        Assert.Equal(HumanInputPolicyResolutionStatus.Invalid, defaultedResult.Status);
        Assert.Equal(HumanInputPolicyResolutionStatus.Unavailable, clockResult.Status);
    }

    private static HumanInputPolicyResolutionTestSource Source()
    {
        var source = new HumanInputPolicyResolutionTestSource();
        source.Results.Add(Timeout().Reference, new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, Timeout(), 1));
        source.Results.Add(Failure().Reference, new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, Failure(), 1));
        return source;
    }

    private static HumanInputPolicyResolutionRequest Request() => new("workspace-one", "graph-one", "revision-one", "node-one", "actor-one", Configuration("timeout-one@revision-one", "failure-one@revision-one"));

    private static GovernedLoopHumanInputNodeConfiguration Configuration(string timeout, string failure)
        => new(1, "response-schema-one", "Collect data.", "Provide data.", new HumanInputResponseSchema(HumanInputResponseKind.Text, 32, null, null, null), HumanInputPrivacyClass.Private, [new HumanInputEligibleRespondent("actor-one", "role-one", "route-one")], new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null), timeout, failure);

    private static HumanInputPolicyArtifact Timeout()
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "timeout-one", "revision-one", HumanInputPolicyKind.ResponseWindow, "workspace-one", "graph-one", "actor-one", 3_600_000, HumanInputTerminalDisposition.Unknown, string.Empty));

    private static HumanInputPolicyArtifact Failure()
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "failure-one", "revision-one", HumanInputPolicyKind.DeadlineDisposition, "workspace-one", "graph-one", "actor-one", null, HumanInputTerminalDisposition.Expired, string.Empty));

    private static readonly DateTimeOffset _at = new(2026, 8, 26, 15, 0, 0, TimeSpan.Zero);
}
