using System.Net;
using System.Net.Sockets;
using System.Text;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Controllers;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class EffectReconciliationHttpIntegrationTests
{
    private const string CaseId = "case-one";
    private const string ContentHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BindingHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task ConfigureServices_composes_one_host_for_effect_reconciliation_and_one_server_authority_provider()
    {
        using var workspace = new TestWorkspace();
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test"]);
        var services = new ServiceCollection();
        services.AddLogging();

        Program.ConfigureServices(services, options);
        await using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<WebAgentRuntimeHost>();
        Assert.Same(host, provider.GetRequiredService<IWebEffectReconciliationRuntime>());
        Assert.IsType<WebEffectReconciliationAuthorizationProvider>(provider.GetRequiredService<IGovernedLoopEffectReconciliationAuthorizationProvider>());
        Assert.Same(
            provider.GetRequiredService<IGovernedLoopEffectReconciliationAuthorizationProvider>(),
            provider.GetRequiredService<IGovernedLoopEffectReconciliationAuthorizationProvider>());
        Assert.Same(host, provider.GetRequiredService<IWebHumanReviewRuntime>());
        Assert.Same(host, provider.GetRequiredService<IWebLoopRuntimeInvoker>());

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IWebEffectReconciliationRuntime) && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IGovernedLoopEffectReconciliationAuthorizationProvider) && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task Local_session_authenticates_effect_reconciliation_routes_and_sets_no_store_on_success_and_errors()
    {
        using var workspace = new TestWorkspace();
        var runtime = new EffectReconciliationControllerTestRuntime();
        await using var app = CreateApp(workspace.RootPath, runtime, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            using var missingToken = await client.GetAsync("/api/effect-reconciliation");
            using var invalidToken = await SendAsync(client, HttpMethod.Get, "/api/effect-reconciliation", "invalid-token");
            using var authorized = await SendAsync(client, HttpMethod.Get, "/api/effect-reconciliation?maximumCount=1&cursor=cursor-one", token);
            using var invalidLow = await SendAsync(client, HttpMethod.Get, "/api/effect-reconciliation?maximumCount=0", token);
            using var invalidHigh = await SendAsync(client, HttpMethod.Get, "/api/effect-reconciliation?maximumCount=51", token);
            using var probes = await SendAsync(client, HttpMethod.Get, "/api/effect-reconciliation/probes?maximumCount=1", token);

            Assert.Equal(HttpStatusCode.Unauthorized, missingToken.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, invalidToken.StatusCode);
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidLow.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, invalidHigh.StatusCode);
            Assert.Equal(HttpStatusCode.OK, probes.StatusCode);
            AssertNoStore(missingToken);
            AssertNoStore(invalidToken);
            AssertNoStore(authorized);
            AssertNoStore(invalidLow);
            AssertNoStore(invalidHigh);
            AssertNoStore(probes);
            Assert.Equal(1, runtime.ListCalls);
            Assert.Equal(1, runtime.ProbeCatalogCalls);
            Assert.Equal(1, runtime.LastListRequest?.MaximumCount);
            Assert.Equal("cursor-one", runtime.LastListRequest?.Cursor);
            Assert.Equal(1, runtime.LastProbePageRequest?.MaximumCount);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Canonical_statuses_are_projected_without_private_runtime_details()
    {
        using var workspace = new TestWorkspace();
        var runtime = new EffectReconciliationControllerTestRuntime();
        await using var app = CreateApp(workspace.RootPath, runtime, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var readPath = $"/api/effect-reconciliation/{CaseId}?caseVersion=3&contentHash={ContentHash}&bindingHash={BindingHash}";
            var resolutionPath = $"/api/effect-reconciliation/{CaseId}/resolution?caseVersion=3&contentHash={ContentHash}&bindingHash={BindingHash}";
            var operationBody = OperationBody();

            runtime.ReadResponse = new GovernedLoopEffectReconciliationReadResult(GovernedLoopEffectReconciliationReadStatus.NotFound, null);
            foreach (var (status, expected) in new[]
            {
                (GovernedLoopEffectReconciliationPageStatus.Ready, HttpStatusCode.OK),
                (GovernedLoopEffectReconciliationPageStatus.Invalid, HttpStatusCode.BadRequest),
                (GovernedLoopEffectReconciliationPageStatus.Corrupt, HttpStatusCode.Conflict),
                (GovernedLoopEffectReconciliationPageStatus.Unavailable, HttpStatusCode.ServiceUnavailable)
            })
            {
                runtime.PageResponse = new GovernedLoopEffectReconciliationPage(status, []);
                using var response = await SendAsync(client, HttpMethod.Get, "/api/effect-reconciliation", token);
                Assert.Equal(expected, response.StatusCode);
                AssertNoStore(response);
            }

            foreach (var (status, expected) in new[]
            {
                (GovernedLoopEffectReconciliationProbeCatalogStatus.Ready, HttpStatusCode.OK),
                (GovernedLoopEffectReconciliationProbeCatalogStatus.Invalid, HttpStatusCode.BadRequest),
                (GovernedLoopEffectReconciliationProbeCatalogStatus.Corrupt, HttpStatusCode.Conflict),
                (GovernedLoopEffectReconciliationProbeCatalogStatus.Unavailable, HttpStatusCode.ServiceUnavailable)
            })
            {
                runtime.ProbeCatalogResponse = new GovernedLoopEffectReconciliationProbeCatalogPage(status, []);
                using var response = await SendAsync(client, HttpMethod.Get, "/api/effect-reconciliation/probes", token);
                Assert.Equal(expected, response.StatusCode);
                AssertNoStore(response);
            }

            foreach (var (status, expected) in new[]
            {
                (GovernedLoopEffectReconciliationReadStatus.NotFound, HttpStatusCode.NotFound),
                (GovernedLoopEffectReconciliationReadStatus.Invalid, HttpStatusCode.BadRequest),
                (GovernedLoopEffectReconciliationReadStatus.Corrupt, HttpStatusCode.Conflict),
                (GovernedLoopEffectReconciliationReadStatus.Unavailable, HttpStatusCode.ServiceUnavailable)
            })
            {
                runtime.ReadResponse = new GovernedLoopEffectReconciliationReadResult(status, null);
                using var response = await SendAsync(client, HttpMethod.Get, readPath, token);
                Assert.Equal(expected, response.StatusCode);
                AssertNoStore(response);
            }

            foreach (var (status, expected) in new[]
            {
                (GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, HttpStatusCode.NotFound),
                (GovernedLoopEffectReconciliationResolutionReadStatus.Invalid, HttpStatusCode.BadRequest),
                (GovernedLoopEffectReconciliationResolutionReadStatus.Corrupt, HttpStatusCode.Conflict),
                (GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, HttpStatusCode.ServiceUnavailable)
            })
            {
                runtime.ResolutionResponse = new GovernedLoopEffectReconciliationResolutionReadResult(status, null);
                using var response = await SendAsync(client, HttpMethod.Get, resolutionPath, token);
                Assert.Equal(expected, response.StatusCode);
                AssertNoStore(response);
            }

            runtime.ReadResponse = new GovernedLoopEffectReconciliationReadResult(GovernedLoopEffectReconciliationReadStatus.NotFound, null);
            foreach (var (status, expected) in new[]
            {
                (GovernedLoopEffectReconciliationOperationStatus.Applied, HttpStatusCode.OK),
                (GovernedLoopEffectReconciliationOperationStatus.Replayed, HttpStatusCode.OK),
                (GovernedLoopEffectReconciliationOperationStatus.Found, HttpStatusCode.OK),
                (GovernedLoopEffectReconciliationOperationStatus.Invalid, HttpStatusCode.BadRequest),
                (GovernedLoopEffectReconciliationOperationStatus.NotFound, HttpStatusCode.NotFound),
                (GovernedLoopEffectReconciliationOperationStatus.Denied, HttpStatusCode.Forbidden),
                (GovernedLoopEffectReconciliationOperationStatus.Conflict, HttpStatusCode.Conflict),
                (GovernedLoopEffectReconciliationOperationStatus.Corrupt, HttpStatusCode.Conflict),
                (GovernedLoopEffectReconciliationOperationStatus.CapacityExceeded, HttpStatusCode.Conflict),
                (GovernedLoopEffectReconciliationOperationStatus.RepairRequired, HttpStatusCode.Conflict),
                (GovernedLoopEffectReconciliationOperationStatus.Unavailable, HttpStatusCode.ServiceUnavailable)
            })
            {
                runtime.OperationResponse = new GovernedLoopEffectReconciliationOperationResult(status, null);
                using var response = await SendJsonAsync(client, HttpMethod.Post, $"/api/effect-reconciliation/{CaseId}/probe", token, operationBody);
                var operationResponseBody = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {operationResponseBody}");
                AssertNoStore(response);
            }

            runtime.ReadException = new InvalidOperationException("private-effect-reconciliation-detail");
            using var failedRead = await SendAsync(client, HttpMethod.Get, readPath, token);
            var failedBody = await failedRead.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failedRead.StatusCode);
            Assert.Contains("effect_reconciliation_unavailable", failedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("private-effect-reconciliation-detail", failedBody, StringComparison.Ordinal);
            AssertNoStore(failedRead);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Body_and_route_identity_are_server_correlated_and_authority_fields_are_not_accepted()
    {
        using var workspace = new TestWorkspace();
        var runtime = new EffectReconciliationControllerTestRuntime();
        await using var app = CreateApp(workspace.RootPath, runtime, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var readPath = $"/api/effect-reconciliation/{CaseId}?caseVersion=3&contentHash={ContentHash}&bindingHash={BindingHash}";

            using var read = await SendAsync(client, HttpMethod.Get, readPath, token);
            Assert.True(read.StatusCode == HttpStatusCode.NotFound, await read.Content.ReadAsStringAsync());
            Assert.Equal(CaseId, runtime.LastReference?.CaseId);
            Assert.Equal(3, runtime.LastReference?.CaseVersion);
            Assert.Equal(ContentHash, runtime.LastReference?.ContentHash);
            Assert.Equal(BindingHash, runtime.LastReference?.BindingHash);

            using var assessed = await SendJsonAsync(client, HttpMethod.Post, $"/api/effect-reconciliation/{CaseId}/assess", token, OperationBody("assessment-one", "safe operator context"));
            Assert.True(assessed.StatusCode == HttpStatusCode.NotFound, await assessed.Content.ReadAsStringAsync());
            Assert.Equal("assessment-one", runtime.LastOperationId);
            Assert.Equal("safe operator context", runtime.LastSafeDetail);

            using var disposed = await SendJsonAsync(client, HttpMethod.Post, $"/api/effect-reconciliation/{CaseId}/dispose", token, DispositionBody());
            Assert.True(disposed.StatusCode == HttpStatusCode.NotFound, await disposed.Content.ReadAsStringAsync());
            Assert.Equal("dispose-one", runtime.LastOperationId);
            Assert.Equal(GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved, runtime.LastDispositionKind);
            Assert.Equal("safe disposition context", runtime.LastSafeDetail);

            using var mismatchedRoute = await SendJsonAsync(client, HttpMethod.Post, "/api/effect-reconciliation/different-case/assess", token, OperationBody());
            Assert.Equal(HttpStatusCode.BadRequest, mismatchedRoute.StatusCode);
            Assert.Equal(1, runtime.AssessCalls);

            var forged = """
                {
                  "case": { "caseId": "case-one", "caseVersion": 3, "contentHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bindingHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
                  "operationId": "forged-operation",
                  "safeDetail": "safe",
                  "actorId": "private-actor",
                  "scopeId": "private-scope",
                  "evidence": "private-evidence"
                }
                """;
            using var forgedBody = await SendJsonAsync(client, HttpMethod.Post, $"/api/effect-reconciliation/{CaseId}/assess", token, forged);
            var forgedResponseBody = await forgedBody.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, forgedBody.StatusCode);
            Assert.DoesNotContain("private-actor", forgedResponseBody, StringComparison.Ordinal);
            Assert.DoesNotContain("private-scope", forgedResponseBody, StringComparison.Ordinal);
            Assert.Equal(1, runtime.AssessCalls);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Uninitialized_workspace_is_projected_as_conflict_for_every_public_route()
    {
        var runtime = new EffectReconciliationControllerTestRuntime { IsWorkspaceInitialized = false };
        var controller = new EffectReconciliationController(runtime);
        var reference = new WebEffectReconciliationCaseReference(CaseId, 3, ContentHash, BindingHash);
        var operation = new WebEffectReconciliationOperationRequest(reference, "operation-one", null);
        var disposition = new WebEffectReconciliationDispositionRequest(reference, "dispose-one", GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved, null);

        AssertConflict(await controller.List());
        AssertConflict(await controller.ListProbes());
        AssertConflict(await controller.Read(CaseId, 3, ContentHash, BindingHash));
        AssertConflict(await controller.ReadResolution(CaseId, 3, ContentHash, BindingHash));
        AssertConflict(await controller.Probe(CaseId, operation));
        AssertConflict(await controller.Assess(CaseId, operation));
        AssertConflict(await controller.Dispose(CaseId, disposition));
    }

    [Fact]
    public async Task Public_actions_redact_runtime_failures_preserve_cancellation_and_reject_malformed_inputs()
    {
        var runtime = new EffectReconciliationControllerTestRuntime();
        var controller = new EffectReconciliationController(runtime);
        var operation = new WebEffectReconciliationOperationRequest(new WebEffectReconciliationCaseReference(CaseId, 3, ContentHash, BindingHash), "operation-one", null);
        var disposition = new WebEffectReconciliationDispositionRequest(operation.Case, "dispose-one", GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved, null);

        runtime.ListException = new ArgumentException("private list detail");
        Assert.IsType<BadRequestObjectResult>(await controller.List());
        runtime.ListException = new InvalidOperationException("private list detail");
        AssertUnavailable(await controller.List());
        runtime.ListException = new OperationCanceledException("private list detail");
        using var listCancellation = new CancellationTokenSource();
        listCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.List(cancellationToken: listCancellation.Token));

        runtime.ProbeCatalogException = new ArgumentException("private probe page detail");
        Assert.IsType<BadRequestObjectResult>(await controller.ListProbes());
        runtime.ProbeCatalogException = new InvalidOperationException("private probe page detail");
        AssertUnavailable(await controller.ListProbes());
        runtime.ProbeCatalogException = new OperationCanceledException("private probe page detail");
        using var probePageCancellation = new CancellationTokenSource();
        probePageCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.ListProbes(cancellationToken: probePageCancellation.Token));
        runtime.ProbeCatalogException = null;
        Assert.IsType<BadRequestObjectResult>(await controller.ListProbes(0));
        Assert.IsType<BadRequestObjectResult>(await controller.ListProbes(51));

        Assert.IsType<BadRequestObjectResult>(await controller.Read(CaseId, 3, "not-a-hash", BindingHash));
        runtime.ReadException = new OperationCanceledException("private read detail");
        using var readCancellation = new CancellationTokenSource();
        readCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.Read(CaseId, 3, ContentHash, BindingHash, readCancellation.Token));
        runtime.ReadException = null;

        Assert.IsType<BadRequestObjectResult>(await controller.ReadResolution(CaseId, 3, ContentHash, "not-a-hash"));
        runtime.ResolutionException = new OperationCanceledException("private resolution detail");
        using var resolutionCancellation = new CancellationTokenSource();
        resolutionCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.ReadResolution(CaseId, 3, ContentHash, BindingHash, resolutionCancellation.Token));
        runtime.ResolutionException = null;
        runtime.ResolutionException = new InvalidOperationException("private resolution detail");
        AssertUnavailable(await controller.ReadResolution(CaseId, 3, ContentHash, BindingHash));
        runtime.ResolutionException = null;

        runtime.ReadResponse = new GovernedLoopEffectReconciliationReadResult(GovernedLoopEffectReconciliationReadStatus.Found, Detail());
        Assert.IsType<OkObjectResult>(await controller.Read(CaseId, 3, ContentHash, BindingHash));
        runtime.ResolutionResponse = new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Found, Resolution());
        Assert.IsType<OkObjectResult>(await controller.ReadResolution(CaseId, 3, ContentHash, BindingHash));

        Assert.IsType<BadRequestObjectResult>(await controller.Probe(CaseId, operation with { SafeDetail = "probe detail is not accepted" }));
        runtime.OperationException = new OperationCanceledException("private probe detail");
        using var probeCancellation = new CancellationTokenSource();
        probeCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.Probe(CaseId, operation, probeCancellation.Token));
        runtime.OperationException = new InvalidOperationException("private assess detail");
        AssertUnavailable(await controller.Assess(CaseId, operation));
        runtime.OperationException = new OperationCanceledException("private dispose detail");
        using var disposeCancellation = new CancellationTokenSource();
        disposeCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.Dispose(CaseId, disposition, disposeCancellation.Token));
        runtime.OperationException = new InvalidOperationException("private dispose detail");
        AssertUnavailable(await controller.Dispose(CaseId, disposition));
        Assert.IsType<BadRequestObjectResult>(await controller.Dispose(CaseId, disposition with { DispositionKind = GovernedLoopEffectReconciliationDispositionKind.Unknown }));

        runtime.OperationException = null;
        runtime.OperationResponse = null!;
        AssertUnavailable(await controller.Probe(CaseId, operation));

        runtime.OperationResponse = new GovernedLoopEffectReconciliationOperationResult(GovernedLoopEffectReconciliationOperationStatus.Applied, null);
        runtime.ReadResponse = new GovernedLoopEffectReconciliationReadResult(GovernedLoopEffectReconciliationReadStatus.Unavailable, null);
        AssertUnavailable(await controller.Probe(CaseId, operation));

        runtime.ReadResponse = new GovernedLoopEffectReconciliationReadResult(GovernedLoopEffectReconciliationReadStatus.Found, Detail());
        Assert.IsType<OkObjectResult>(await controller.Probe(CaseId, operation));
    }

    private static WebApplication CreateApp(string rootPath, EffectReconciliationControllerTestRuntime runtime, out WebRunOptions options)
    {
        var port = GetFreePort();
        options = WebRunOptions.FromArguments(["--workdir", rootPath, "--port", port.ToString(), "--model", "gpt-test"]);
        var builder = Program.CreateBuilder(["--workdir", rootPath, "--port", port.ToString(), "--model", "gpt-test"], options);
        builder.Services.AddSingleton<IWebEffectReconciliationRuntime>(runtime);
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
    }

    private static string OperationBody(string operationId = "operation-one", string? safeDetail = null)
    {
        var detail = safeDetail is null ? string.Empty : $",\n  \"safeDetail\": \"{safeDetail}\"";
        return $$"""
            {
              "case": {
                "caseId": "{{CaseId}}",
                "caseVersion": 3,
                "contentHash": "{{ContentHash}}",
                "bindingHash": "{{BindingHash}}"
              },
              "operationId": "{{operationId}}"{{detail}}
            }
            """;
    }

    private static string DispositionBody()
        => $$"""
            {
              "case": {
                "caseId": "{{CaseId}}",
                "caseVersion": 3,
                "contentHash": "{{ContentHash}}",
                "bindingHash": "{{BindingHash}}"
              },
              "operationId": "dispose-one",
              "dispositionKind": "quarantine-unresolved",
              "safeDetail": "safe disposition context"
            }
            """;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string path, string token, string json)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(WebSessionSecurity.HeaderName, token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore == true, $"Expected no-store cache policy for {(int)response.StatusCode} {response.RequestMessage?.RequestUri}.");
    }

    private static void AssertConflict(IActionResult result)
    {
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    private static void AssertUnavailable(IActionResult result)
    {
        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.DoesNotContain("private", unavailable.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static GovernedLoopEffectReconciliationCaseDetail Detail()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var reference = new GovernedLoopEffectReconciliationCaseReference(CaseId, 3, ContentHash, BindingHash);
        var contract = new GovernedLoopEffectReconciliationContractProjection("contract-one", 1, ContentHash, "probe-one", 1, BindingHash);
        return new GovernedLoopEffectReconciliationCaseDetail(reference, GovernedLoopEffectReconciliationCasePosture.Open, contract, [], [], [], null, null, [], now, now);
    }

    private static GovernedLoopEffectReconciliationResolutionProjection Resolution()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        return new GovernedLoopEffectReconciliationResolutionProjection("resolution-one", ContentHash, BindingHash, GovernedLoopEffectReconciliationResolutionOutcome.NotApplied, null, null, now, ContentHash);
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

internal sealed class EffectReconciliationControllerTestRuntime : IWebEffectReconciliationRuntime
{
    public bool IsWorkspaceInitialized { get; set; } = true;

    public GovernedLoopEffectReconciliationPage PageResponse { get; set; } = new(GovernedLoopEffectReconciliationPageStatus.Ready, []);

    public GovernedLoopEffectReconciliationReadResult ReadResponse { get; set; } = new(GovernedLoopEffectReconciliationReadStatus.NotFound, null);

    public GovernedLoopEffectReconciliationProbeCatalogPage ProbeCatalogResponse { get; set; } = new(GovernedLoopEffectReconciliationProbeCatalogStatus.Ready, []);

    public GovernedLoopEffectReconciliationResolutionReadResult ResolutionResponse { get; set; } = new(GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, null);

    public GovernedLoopEffectReconciliationOperationResult OperationResponse { get; set; } = new(GovernedLoopEffectReconciliationOperationStatus.NotFound, null);

    public Exception? ReadException { get; set; }

    public Exception? ResolutionException { get; set; }

    public Exception? ListException { get; set; }

    public Exception? ProbeCatalogException { get; set; }

    public Exception? OperationException { get; set; }

    public int ListCalls { get; private set; }

    public int ProbeCatalogCalls { get; private set; }

    public int AssessCalls { get; private set; }

    public GovernedLoopEffectReconciliationPageRequest? LastListRequest { get; private set; }

    public GovernedLoopEffectReconciliationPageRequest? LastProbePageRequest { get; private set; }

    public GovernedLoopEffectReconciliationCaseReference? LastReference { get; private set; }

    public string? LastOperationId { get; private set; }

    public string? LastSafeDetail { get; private set; }

    public Task<GovernedLoopEffectReconciliationPage> ListAsync(GovernedLoopEffectReconciliationPageRequest request, CancellationToken cancellationToken = default)
    {
        ListCalls++;
        LastListRequest = request;
        if (ListException is not null)
        {
            throw ListException;
        }

        return Task.FromResult(PageResponse);
    }

    public Task<GovernedLoopEffectReconciliationReadResult> ReadAsync(GovernedLoopEffectReconciliationCaseReference reference, CancellationToken cancellationToken = default)
    {
        LastReference = reference;
        if (ReadException is not null)
        {
            throw ReadException;
        }

        return Task.FromResult(ReadResponse);
    }

    public Task<GovernedLoopEffectReconciliationProbeCatalogPage> ListProbeContractsAsync(GovernedLoopEffectReconciliationPageRequest request, CancellationToken cancellationToken = default)
    {
        ProbeCatalogCalls++;
        LastProbePageRequest = request;
        if (ProbeCatalogException is not null)
        {
            throw ProbeCatalogException;
        }

        return Task.FromResult(ProbeCatalogResponse);
    }

    public Task<GovernedLoopEffectReconciliationOperationResult> ProbeAsync(string operationId, GovernedLoopEffectReconciliationCaseReference reference, CancellationToken cancellationToken = default)
    {
        LastOperationId = operationId;
        LastReference = reference;
        if (OperationException is not null)
        {
            throw OperationException;
        }

        return Task.FromResult(OperationResponse);
    }

    public Task<GovernedLoopEffectReconciliationOperationResult> AssessAsync(string operationId, GovernedLoopEffectReconciliationCaseReference reference, string? safeDetail = null, CancellationToken cancellationToken = default)
    {
        AssessCalls++;
        LastOperationId = operationId;
        LastReference = reference;
        LastSafeDetail = safeDetail;
        if (OperationException is not null)
        {
            throw OperationException;
        }

        return Task.FromResult(OperationResponse);
    }

    public Task<GovernedLoopEffectReconciliationOperationResult> ApplyDispositionAsync(string operationId, GovernedLoopEffectReconciliationCaseReference reference, GovernedLoopEffectReconciliationDispositionKind kind, string? safeDetail = null, CancellationToken cancellationToken = default)
    {
        LastOperationId = operationId;
        LastReference = reference;
        LastSafeDetail = safeDetail;
        LastDispositionKind = kind;
        if (OperationException is not null)
        {
            throw OperationException;
        }

        return Task.FromResult(OperationResponse);
    }

    public Task<GovernedLoopEffectReconciliationResolutionReadResult> ReadResolutionAsync(GovernedLoopEffectReconciliationCaseReference reference, CancellationToken cancellationToken = default)
    {
        LastReference = reference;
        if (ResolutionException is not null)
        {
            throw ResolutionException;
        }

        return Task.FromResult(ResolutionResponse);
    }

    public GovernedLoopEffectReconciliationDispositionKind? LastDispositionKind { get; private set; }
}
