using System.Net;
using System.Net.Sockets;
using System.Text;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class HumanReviewsHttpIntegrationTests
{
    private const string SensitiveCanary = "human-review-sensitive-canary";

    [Fact]
    public async Task Local_session_authenticates_human_review_routes_and_sets_no_store_on_success_and_errors()
    {
        using var workspace = new TestWorkspace();
        var runtime = new HumanReviewControllerTestRuntime();
        await using var app = CreateApp(workspace.RootPath, runtime, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            using var missingToken = await client.GetAsync("/api/human-reviews");
            using var invalidToken = await SendAsync(client, HttpMethod.Get, "/api/human-reviews", "invalid-token");
            using var authorized = await SendAsync(client, HttpMethod.Get, "/api/human-reviews?maximumCount=1", token);
            using var invalidPage = await SendAsync(client, HttpMethod.Get, "/api/human-reviews?maximumCount=51", token);

            Assert.Equal(HttpStatusCode.Unauthorized, missingToken.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, invalidToken.StatusCode);
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            AssertNoStore(authorized);
            Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
            AssertNoStore(invalidPage);
            Assert.Equal(1, runtime.ListCalls);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Http_routes_project_canonical_statuses_and_keep_failure_bodies_detached()
    {
        using var workspace = new TestWorkspace();
        var runtime = new HumanReviewControllerTestRuntime();
        await using var app = CreateApp(workspace.RootPath, runtime, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;

            runtime.ReadResponse = new HumanReviewReadResult(HumanReviewReadStatus.NotFound);
            using var notFound = await SendAsync(client, HttpMethod.Get, "/api/human-reviews/run-1", token);
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            AssertNoStore(notFound);

            runtime.EvidenceResponse = new HumanReviewEvidenceReadResult(HumanReviewEvidenceReadStatus.Corrupt, [], null);
            using var corruptEvidence = await SendAsync(client, HttpMethod.Get, "/api/human-reviews/run-1/evidence", token);
            Assert.Equal(HttpStatusCode.Conflict, corruptEvidence.StatusCode);
            AssertNoStore(corruptEvidence);

            runtime.PostureResponse = new HumanReviewRuntimePostureReadResult(HumanReviewReadStatus.Unknown, null);
            using var unavailablePosture = await SendAsync(client, HttpMethod.Get, "/api/human-reviews/run-1/posture", token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailablePosture.StatusCode);
            AssertNoStore(unavailablePosture);

            runtime.DecisionResponse = new HumanReviewDecisionResult(HumanReviewDecisionStatus.Denied, "operation-1", null);
            using var denied = await SendJsonAsync(client, HttpMethod.Post, "/api/human-reviews/run-1/reject", token, "{\"expectedLifecycleVersion\":1,\"operationId\":\"operation-1\"}");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            AssertNoStore(denied);

            runtime.DecisionResponse = new HumanReviewDecisionResult(HumanReviewDecisionStatus.Conflict, "operation-2", null);
            using var conflict = await SendJsonAsync(client, HttpMethod.Post, "/api/human-reviews/run-1/cancel", token, "{\"expectedLifecycleVersion\":1,\"operationId\":\"operation-2\"}");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            AssertNoStore(conflict);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Strict_json_rejects_unknown_duplicate_and_forged_authority_members_without_dispatch()
    {
        using var workspace = new TestWorkspace();
        var runtime = new HumanReviewControllerTestRuntime();
        await using var app = CreateApp(workspace.RootPath, runtime, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var forged = $$"""
                {
                  "expectedLifecycleVersion": 1,
                  "operationId": "operation-forged",
                  "detail": null,
                  "actor": "{{SensitiveCanary}}",
                  "role": "governed-reviewer",
                  "scope": "workspace:all",
                  "grant": "{{SensitiveCanary}}",
                  "workspace": "{{SensitiveCanary}}",
                  "connection": "{{SensitiveCanary}}"
                }
                """;
            using var unknownMembers = await SendJsonAsync(client, HttpMethod.Post, "/api/human-reviews/run-1/approve", token, forged);
            var duplicate = "{\"expectedLifecycleVersion\":1,\"expectedLifecycleVersion\":1,\"operationId\":\"operation-duplicate\"}";
            using var duplicateMembers = await SendJsonAsync(client, HttpMethod.Post, "/api/human-reviews/run-1/approve", token, duplicate);
            var unknownBody = await unknownMembers.Content.ReadAsStringAsync();
            var duplicateBody = await duplicateMembers.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, unknownMembers.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, duplicateMembers.StatusCode);
            Assert.DoesNotContain(SensitiveCanary, unknownBody, StringComparison.Ordinal);
            Assert.DoesNotContain(SensitiveCanary, duplicateBody, StringComparison.Ordinal);
            Assert.Equal(0, runtime.DecisionCalls);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Request_body_cap_rejects_oversized_decisions_before_runtime_dispatch()
    {
        using var workspace = new TestWorkspace();
        var runtime = new HumanReviewControllerTestRuntime();
        await using var app = CreateApp(workspace.RootPath, runtime, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var oversized = $$"""{"expectedLifecycleVersion":1,"operationId":"operation-large","detail":"{{new string('x', 17_000)}}"}""";
            using var response = await SendJsonAsync(client, HttpMethod.Post, "/api/human-reviews/run-1/request-information", token, oversized);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal((HttpStatusCode)413, response.StatusCode);
            Assert.DoesNotContain(SensitiveCanary, body, StringComparison.Ordinal);
            Assert.Equal(0, runtime.DecisionCalls);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Runtime_failures_are_503_without_private_exception_details_or_canaries()
    {
        using var workspace = new TestWorkspace();
        var runtime = new HumanReviewControllerTestRuntime { ReadException = new InvalidOperationException(SensitiveCanary) };
        await using var app = CreateApp(workspace.RootPath, runtime, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            using var response = await SendAsync(client, HttpMethod.Get, "/api/human-reviews/run-1", token);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            AssertNoStore(response);
            Assert.Contains("human_review_runtime_unavailable", body, StringComparison.Ordinal);
            Assert.DoesNotContain(SensitiveCanary, body, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static Microsoft.AspNetCore.Builder.WebApplication CreateApp(string rootPath, HumanReviewControllerTestRuntime runtime, out WebRunOptions options)
    {
        var port = GetFreePort();
        var arguments = new[] { "--workdir", rootPath, "--port", port.ToString(), "--model", "gpt-test" };
        options = WebRunOptions.FromArguments(arguments);
        var builder = Program.CreateBuilder(arguments, options);
        builder.Services.AddSingleton<IWebHumanReviewRuntime>(runtime);
        builder.Services.AddSingleton<IWebHumanReviewNotifier, HumanReviewControllerTestNotifier>();
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
    }

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

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
