using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class WebGovernedLoopBackgroundLifetimeTests
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Hosted_lifetime_defers_until_workspace_initialization_then_retries_to_ready()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await WebBackgroundLifetimeCodexExecutable.CreateAsync(workspace);
        await using var app = CreateApp(workspace.RootPath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var before = await ReadStatusAsync(client);
            using var session = await client.GetAsync("/api/session");
            using var initialize = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/init");
            initialize.Headers.Add("Cookie", SessionCookie(session));
            initialize.Content = JsonContent.Create(new { }, options: _jsonOptions);
            using var initialized = await client.SendAsync(initialize);

            Assert.Equal(WebGovernedLoopBackgroundPosture.Unavailable, before.BackgroundPosture);
            Assert.True(initialized.IsSuccessStatusCode);
            Assert.Equal(WebGovernedLoopBackgroundPosture.Ready, (await WaitForPostureAsync(client, WebGovernedLoopBackgroundPosture.Ready)).BackgroundPosture);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Background_worker_processes_governed_trigger_after_the_only_browser_connection_disconnects()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await WebBackgroundLifetimeCodexExecutable.CreateAsync(workspace);
        await WorkspaceInitializer.ForWeb().InitializeAsync(workspace.RootPath);
        await using var app = CreateApp(workspace.RootPath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            Assert.Equal(WebGovernedLoopBackgroundPosture.Ready, (await WaitForPostureAsync(client, WebGovernedLoopBackgroundPosture.Ready)).BackgroundPosture);
            using var session = await client.GetAsync("/api/session");
            var sessionCookie = SessionCookie(session);
            using var socket = new ClientWebSocket();
            socket.Options.Cookies = new CookieContainer();
            socket.Options.Cookies.SetCookies(new Uri(options.Url), sessionCookie);
            await socket.ConnectAsync(ToHubUri(options.Url), CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test disconnect", CancellationToken.None);

            var queue = new TriggerQueueStore(new WorkspacePaths(workspace.RootPath), TriggerQueueQuota.Runtime);
            var deliveryId = await AdmitGovernedBackgroundTriggerAsync(workspace, queue);
            var completed = await WaitForTriggerStateAsync(queue, deliveryId, TriggerQueueEntryState.DispatchRejected);

            Assert.Equal(TriggerQueueEntryState.DispatchRejected, completed.State);
            Assert.Equal(TriggerDispatchOutcome.Rejected, completed.Dispatch?.Outcome);
            Assert.NotNull(completed.Dispatch?.OperationId);
            Assert.NotNull(completed.WorkerLease?.ReleasedAtUtc);
            Assert.Contains("governed publication", completed.Dispatch?.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(WebGovernedLoopBackgroundPosture.Ready, (await ReadStatusAsync(client)).BackgroundPosture);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Shutdown_reports_stopped_and_disposes_the_pinned_runtime_idempotently()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await WebBackgroundLifetimeCodexExecutable.CreateAsync(workspace);
        await WorkspaceInitializer.ForWeb().InitializeAsync(workspace.RootPath);
        var app = CreateApp(workspace.RootPath, codexPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            Assert.Equal(WebGovernedLoopBackgroundPosture.Ready, (await WaitForPostureAsync(client, WebGovernedLoopBackgroundPosture.Ready)).BackgroundPosture);
            var runtimeHost = app.Services.GetRequiredService<WebAgentRuntimeHost>();
            await runtimeHost.SendMessageAsync("background runtime disposal", (_, _) => Task.CompletedTask);
            await WaitForLinesAsync(WebBackgroundLifetimeCodexExecutable.StartedPath(workspace), 1);
            await app.StopAsync();
            Assert.Equal(WebGovernedLoopBackgroundPosture.Stopped, runtimeHost.GetStatus().BackgroundPosture);
            await app.DisposeAsync();
            await app.DisposeAsync();

            Assert.Single(await File.ReadAllLinesAsync(WebBackgroundLifetimeCodexExecutable.StartedPath(workspace)));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static WebApplication CreateApp(string rootPath, string codexPath, out WebRunOptions options)
    {
        var port = GetFreePort();
        var arguments = new[] { "--workdir", rootPath, "--port", port.ToString(), "--model", "gpt-test", "--codex-path", codexPath };
        options = WebRunOptions.FromArguments(arguments);
        var builder = Program.CreateBuilder(arguments, options);
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    private static async Task<WebStatus> ReadStatusAsync(HttpClient client)
    {
        return await client.GetFromJsonAsync<WebStatus>("/api/status", _jsonOptions)
            ?? throw new InvalidOperationException("The Web status response was empty.");
    }

    private static async Task<WebStatus> WaitForPostureAsync(HttpClient client, WebGovernedLoopBackgroundPosture posture)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var status = await ReadStatusAsync(client);
            if (status.BackgroundPosture == posture)
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The Web background posture did not reach `{posture}`.");
    }

    private static async Task WaitForLinesAsync(string path, int count)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (File.Exists(path) && (await File.ReadAllLinesAsync(path)).Length >= count)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The tracked Codex process did not write {count} line(s) to `{path}`.");
    }

    private static async Task<string> AdmitGovernedBackgroundTriggerAsync(TestWorkspace workspace, TriggerQueueStore queue)
    {
        var now = DateTimeOffset.UtcNow;
        var deliveryText = "web-background-delivery";
        var deduplicationText = "web-background-deduplication";
        Assert.True(TriggerDeliveryId.TryParse(deliveryText, out var deliveryId));
        Assert.True(TriggerDeduplicationId.TryParse(deduplicationText, out var deduplicationId));
        Assert.True(TriggerDeliveryFactory.TryCreateRedeliveryEvidence(1, 1, deliveryId, out var redelivery, out _));

        var descriptor = Assert.Single(BuiltInCapabilityCatalog.Descriptors, item => item.Id.Value == "org.embodysense/triggers/time");
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var descriptorIdentity, out _));
        var adapter = new TriggerAdapterReference(descriptorIdentity!, descriptor.Implementation);
        var revision = GovernedLoopRevisionReference.Create(1, "web-background-graph", "revision-1", new string('a', 64));
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, revision, "web-background-publish", new string('b', 64));
        Assert.True(AuthorityGrantId.TryParse("web-background-grant", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var grantRevision, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateGovernedLoopReference(
            publication,
            new AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + new string('c', 64)),
            out var loop,
            out _));
        Assert.True(AuthorityActorId.TryParse("owner", out var actorId, out _));
        var workspaceId = CapabilityWorkspaceScopeId.Create(workspace.RootPath)["workspace-sha256:".Length..];
        Assert.True(TriggerDeliveryFactory.TryCreateActorContext(actorId, "web", workspaceId, "default-assistant", out var actorContext, out _));
        Assert.True(AuthorityProfileId.TryParse("trigger-operator", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        var profile = new AuthorityProfileReference(profileId!, profileRevision!);
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(
            1,
            AuthorityBoundaryDecision.Direct,
            [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Direct, AuthorityBoundaryReason.NoBoundary)],
            [profile],
            now.AddSeconds(-1),
            out var boundaryReceipt,
            out _));
        var authority = new TriggerAuthorityEvidence(profile, boundaryReceipt!);
        Assert.True(TriggerDeliveryFactory.TryCreateTemporalEvidence(
            now.AddSeconds(-2),
            now.AddSeconds(-1),
            now.AddSeconds(-3),
            null,
            null,
            null,
            null,
            out var temporal,
            out _));
        Assert.True(TriggerDeliveryFactory.TryCreateInlinePayload("background-work"u8.ToArray(), out var payload, out _));
        Assert.True(TriggerDeliveryFactory.TryCreateEnvelope(
            1,
            deliveryId,
            deduplicationId,
            TriggerKind.Webhook,
            adapter,
            loop,
            actorContext,
            authority,
            temporal,
            payload,
            redelivery,
            false,
            null,
            TriggerAdmissionStatus.Unknown,
            TriggerAdmissionReason.Unknown,
            out var envelope,
            out _));
        Assert.True(TriggerDeliveryAdmissionRequestFactory.TryCreate(
            envelope,
            envelope!.Loop,
            envelope.Adapter,
            true,
            envelope.ActorContext,
            envelope.Authority,
            now,
            out var deliveryRequest,
            out _));
        var admission = await new TriggerQueueAdmissionService(new TriggerDeliveryAdmissionService(queue), queue).AdmitAsync(
            TriggerQueueAdmissionRequestFactory.Create(deliveryRequest!, TriggerQueueAdmissionMode.Queued, TriggerQueuePriority.Normal));

        Assert.Equal(TriggerQueueAdmissionStatus.Queued, admission.Status);
        return deliveryId!.Value;
    }

    private static async Task<TriggerQueueEntry> WaitForTriggerStateAsync(TriggerQueueStore queue, string deliveryId, TriggerQueueEntryState state)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var entry = (await queue.GetSnapshotAsync(DateTimeOffset.UtcNow)).Entries.SingleOrDefault(item => item.DeliveryId.Value == deliveryId);
            if (entry?.State == state)
            {
                return entry;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The governed background delivery `{deliveryId}` did not reach `{state}`.");
    }

    private static string SessionCookie(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
    }

    private static Uri ToHubUri(string baseUrl)
    {
        var builder = new UriBuilder(baseUrl) { Scheme = Uri.UriSchemeWs, Path = "/hubs/session" };
        return builder.Uri;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
