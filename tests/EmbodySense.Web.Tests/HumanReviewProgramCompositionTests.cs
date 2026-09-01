using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using CommonRunStatus = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunStatus;
using CommonGovernedLoopFrontierStatus = EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopFrontierStatus;
using CommonRunStoreStatus = EmbodySense.Core.Application.Loops.Models.CustomLoopRunStoreStatus;
using StartupDecisionKind = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionKind;
using StartupDecisionStatus = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionStatus;
using StartupDecisionDisposition = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionOperationDisposition;
using StartupLifecycleStatus = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewLifecycleStatus;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class HumanReviewProgramCompositionTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
    };

    [Fact]
    public async Task Program_composition_decides_a_pending_review_through_server_authority_and_durable_store()
    {
        using var workspace = new TestWorkspace();
        var runId = "run-web-program-composed";
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = CreateApp(workspace, codexPath, out var options, out var host);
        await host.InitializeWorkspaceAsync();
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(runId, "admission-" + runId, materializedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            using var beforeResponse = await SendAsync(client, HttpMethod.Get, $"/api/human-reviews/{runId}", token);
            var beforeBody = await beforeResponse.Content.ReadAsStringAsync();
            var before = JsonSerializer.Deserialize<HumanReviewReadResult>(beforeBody, _jsonOptions);
            var beforeDetail = Assert.IsType<HumanReviewDetail>(before?.Detail);
            using var decisionResponse = await SendJsonAsync(client, $"/api/human-reviews/{runId}/approve", token, JsonSerializer.Serialize(new WebHumanReviewDecisionRequest(checked((int)beforeDetail.Summary.LifecycleVersion), "web-program-decision", null), _jsonOptions));
            var decisionBody = await decisionResponse.Content.ReadAsStringAsync();
            var decision = JsonSerializer.Deserialize<HumanReviewDecisionResult>(decisionBody, _jsonOptions);

            using var afterResponse = await SendAsync(client, HttpMethod.Get, $"/api/human-reviews/{runId}", token);
            var afterBody = await afterResponse.Content.ReadAsStringAsync();
            var after = JsonSerializer.Deserialize<HumanReviewReadResult>(afterBody, _jsonOptions);
            var afterDetail = Assert.IsType<HumanReviewDetail>(after?.Detail);
            var durable = await new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath)).GetAsync(runId);
            var durableDecision = Assert.Single(Assert.IsType<HumanReviewRunState>(durable?.HumanReview).AcceptedDecisions);

            Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
            Assert.Equal(StartupLifecycleStatus.Pending, beforeDetail.Summary.LifecycleStatus);
            Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
            Assert.Equal(StartupDecisionStatus.Accepted, decision?.Status);
            Assert.Equal("web-program-decision", decision?.OperationId);
            Assert.NotNull(decision?.Evidence);
            Assert.Equal(StartupDecisionDisposition.Accepted, decision!.Evidence!.Disposition);
            Assert.Equal(StartupDecisionKind.Approve, decision.Evidence.DecisionKind);
            Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
            Assert.Equal(StartupLifecycleStatus.Approved, afterDetail.Summary.LifecycleStatus);
            Assert.Equal(WorkspaceActors.Web, durableDecision.AuthenticatedActorId);
            Assert.Equal(GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId, durableDecision.ReviewerRoleId);
            Assert.Equal("web-program-decision", durableDecision.DecisionOperationId);
            Assert.Empty(app.Services.GetRequiredService<WebApprovalCoordinator>().GetPending());
            Assert.DoesNotContain("actorId", decisionBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("connection", decisionBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grant", decisionBody, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(afterDetail.Decisions.Single().OperationId, durableDecision.DecisionOperationId);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static WebApplication CreateApp(TestWorkspace workspace, string codexPath, out WebRunOptions options, out WebAgentRuntimeHost host)
    {
        var port = GetFreePort();
        var arguments = new[] { "--workdir", workspace.RootPath, "--port", port.ToString(), "--model", "gpt-test", "--codex-path", codexPath };
        options = WebRunOptions.FromArguments(arguments);
        var builder = Program.CreateBuilder(arguments, options);
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        host = app.Services.GetRequiredService<WebAgentRuntimeHost>();
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, string path, string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task PersistPendingHumanReviewAsync(TestWorkspace workspace, CustomLoopRunRecord blueprint)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var finalFrontier = blueprint.Frontier ?? throw new InvalidOperationException("The canonical recovery test run did not retain a frontier.");
        var review = finalFrontier.Payload.Nodes.Single(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var initialReview = GovernedLoopNodeExecutionEvidence.CreateActivation(review.ActivationOrdinal, review.PlanOrdinal, review.VisitOrdinal, review.NodeId, review.Descriptor, review.IncomingControlEdgeIds, review.OutgoingControlEdgeIds, GovernedLoopNodeExecutionStatus.Ready);
        var initialFrontier = GovernedLoopFrontierPosture.Create(finalFrontier.Binding, finalFrontier.WorkspaceId, finalFrontier.GraphArtifactHash, finalFrontier.GraphLayoutHash, finalFrontier.AdmissionReceiptHash, 1, finalFrontier.Payload.ConcurrencyCeiling, CommonGovernedLoopFrontierStatus.Active, [finalFrontier.Payload.Nodes[0], initialReview], blueprint.CreatedAtUtc, string.Empty);
        var admitted = blueprint with
        {
            LifecycleVersion = 1,
            Status = CommonRunStatus.Admitted,
            UpdatedAtUtc = blueprint.CreatedAtUtc,
            CompletedAtUtc = null,
            ExecutionClock = CustomLoopExecutionClock.NotStarted(),
            Events = blueprint.Events.Take(2).ToArray(),
            Frontier = initialFrontier,
            Checkpoint = CustomLoopRunCheckpoint.Start(),
            HumanReview = null,
            WaitEvidence = [],
            HumanInputWaitingCheckpoints = [],
            FinalOutput = null,
            FailureCode = null,
            FailureDetail = null,
        };
        Assert.True(CustomLoopRunValidator.Validate(admitted).IsValid);
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(CommonRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        var running = CreateRunning(admitted, blueprint);
        Assert.Equal(CommonRunStoreStatus.Updated, (await store.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var started = CreateStarted(running, blueprint);
        Assert.Equal(CommonRunStoreStatus.Updated, (await store.UpdateAsync(started, running.LifecycleVersion)).Status);
        var admission = await new HumanReviewAdmissionService(store).AdmitAsync(new HumanReviewAdmissionCommand(started.Id, started.LifecycleVersion, blueprint.HumanReview!.Request, blueprint.Frontier!, blueprint.Events[3]));
        Assert.Equal(CommonRunStoreStatus.Updated, admission.Status);
    }

    private static CustomLoopRunRecord CreateRunning(CustomLoopRunRecord admitted, CustomLoopRunRecord blueprint)
    {
        var finalFrontier = blueprint.Frontier ?? throw new InvalidOperationException("The canonical recovery test run did not retain a frontier.");
        var finalReview = finalFrontier.Payload.Nodes.Single(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var readyReview = GovernedLoopNodeExecutionEvidence.CreateActivation(finalReview.ActivationOrdinal, finalReview.PlanOrdinal, finalReview.VisitOrdinal, finalReview.NodeId, finalReview.Descriptor, finalReview.IncomingControlEdgeIds, finalReview.OutgoingControlEdgeIds, GovernedLoopNodeExecutionStatus.Ready);
        var updatedAtUtc = blueprint.Events[2].TimestampUtc;
        var frontier = GovernedLoopFrontierPosture.Create(finalFrontier.Binding, finalFrontier.WorkspaceId, finalFrontier.GraphArtifactHash, finalFrontier.GraphLayoutHash, finalFrontier.AdmissionReceiptHash, admitted.Frontier!.Payload.FrontierVersion, finalFrontier.Payload.ConcurrencyCeiling, CommonGovernedLoopFrontierStatus.Active, [finalFrontier.Payload.Nodes[0], readyReview], admitted.Frontier.Payload.UpdatedAtUtc, string.Empty);
        return admitted with { LifecycleVersion = 2, Status = CommonRunStatus.Running, UpdatedAtUtc = updatedAtUtc, ExecutionClock = new CustomLoopExecutionClock(0, updatedAtUtc), Frontier = frontier, Events = [.. admitted.Events, blueprint.Events[2] with { ControlExpectedLifecycleVersion = admitted.LifecycleVersion }] };
    }

    private static CustomLoopRunRecord CreateStarted(CustomLoopRunRecord running, CustomLoopRunRecord blueprint)
    {
        var finalFrontier = blueprint.Frontier ?? throw new InvalidOperationException("The canonical recovery test run did not retain a frontier.");
        var finalReview = finalFrontier.Payload.Nodes.Single(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var startedReview = GovernedLoopNodeExecutionEvidence.CreateActivation(finalReview.ActivationOrdinal, finalReview.PlanOrdinal, finalReview.VisitOrdinal, finalReview.NodeId, finalReview.Descriptor, finalReview.IncomingControlEdgeIds, finalReview.OutgoingControlEdgeIds, GovernedLoopNodeExecutionStatus.Running, finalReview.Attempt, finalReview.AttemptOperationId);
        var updatedAtUtc = blueprint.Events[3].TimestampUtc.AddMinutes(-1);
        var frontier = GovernedLoopFrontierPosture.Create(finalFrontier.Binding, finalFrontier.WorkspaceId, finalFrontier.GraphArtifactHash, finalFrontier.GraphLayoutHash, finalFrontier.AdmissionReceiptHash, blueprint.HumanReview!.Request.Binding.FrontierVersion - 1, finalFrontier.Payload.ConcurrencyCeiling, CommonGovernedLoopFrontierStatus.Active, [finalFrontier.Payload.Nodes[0], startedReview], updatedAtUtc, string.Empty);
        return running with { LifecycleVersion = 3, UpdatedAtUtc = updatedAtUtc, Frontier = frontier };
    }
}
