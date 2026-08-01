using EmbodySense.Web;
using EmbodySense.Core.Startup.Configuration.Models;
using EmbodySense.Core.Startup.Governance;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Startup.Configuration;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmbodySense.Web.Tests;

public sealed class WebApiControllerTests
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Configured_app_serves_status_init_and_approval_endpoints()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            using var unauthenticatedClient = new HttpClient { BaseAddress = new Uri(options.Url) };
            using var sessionResponse = await client.GetAsync("/api/session");
            var session = await sessionResponse.Content.ReadFromJsonAsync<WebSessionInfo>(_jsonOptions);
            var sessionCookie = SessionCookie(sessionResponse);
            using var beforeResponse = await client.GetAsync("/api/status");
            var before = await beforeResponse.Content.ReadFromJsonAsync<WebStatus>(_jsonOptions);
            using var indexResponse = await client.GetAsync("/");
            var index = await indexResponse.Content.ReadAsStringAsync();
            using var faviconResponse = await client.GetAsync("/favicon.svg");
            var rejectedInit = await unauthenticatedClient.PostAsJsonAsync("/api/workspace/init", new { }, _jsonOptions);
            var rejectedQueryTokenConfiguration = await unauthenticatedClient.GetAsync("/api/configuration?access_token=rejected-query-token");
            var initRequest = new HttpRequestMessage(HttpMethod.Post, "/api/workspace/init");
            initRequest.Headers.Add("Cookie", sessionCookie);
            initRequest.Content = JsonContent.Create(new { }, options: _jsonOptions);
            var initialized = await client.SendAsync(initRequest);
            var after = await initialized.Content.ReadFromJsonAsync<WebStatus>(_jsonOptions);
            var approvalsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/approvals/pending");
            approvalsRequest.Headers.Add("Cookie", sessionCookie);
            var approvalsResponse = await client.SendAsync(approvalsRequest);
            var approvals = await approvalsResponse.Content.ReadFromJsonAsync<WebPendingApproval[]>(_jsonOptions);
            var rejectedConfiguration = await unauthenticatedClient.GetAsync("/api/configuration");
            var configurationRequest = new HttpRequestMessage(HttpMethod.Get, "/api/configuration");
            configurationRequest.Headers.Add("Cookie", sessionCookie);
            var configurationResponse = await client.SendAsync(configurationRequest);
            var configuration = await configurationResponse.Content.ReadFromJsonAsync<WorkspaceConfigurationSnapshot>(_jsonOptions);
            var missingApproval = new HttpRequestMessage(HttpMethod.Post, "/api/approvals/missing");
            missingApproval.Headers.Add("Cookie", sessionCookie);
            missingApproval.Content = JsonContent.Create(new WebApprovalDecision(true, null), options: _jsonOptions);
            var missingApprovalResponse = await client.SendAsync(missingApproval);

            Assert.False(before!.Initialized);
            Assert.Equal("uninitialized", before.InitializationState);
            Assert.Null(before.InitializationOutcome);
            Assert.False(string.IsNullOrWhiteSpace(session!.GenerationId));
            Assert.DoesNotContain(app.Services.GetRequiredService<WebSessionSecurity>().Token, await sessionResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Contains("HttpOnly", sessionResponse.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SameSite=Strict", sessionResponse.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("no-store", sessionResponse.Headers.CacheControl?.ToString());
            Assert.True(beforeResponse.Headers.TryGetValues("Content-Security-Policy", out var csp));
            Assert.Equal("default-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'", csp.Single());
            Assert.DoesNotContain("ws://", csp.Single(), StringComparison.Ordinal);
            Assert.True(beforeResponse.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeOptions));
            Assert.Equal("nosniff", contentTypeOptions.Single());
            Assert.True(beforeResponse.Headers.TryGetValues("Referrer-Policy", out var referrerPolicy));
            Assert.Equal("no-referrer", referrerPolicy.Single());
            Assert.True(indexResponse.IsSuccessStatusCode);
            Assert.Contains("<link rel=\"icon\" type=\"image/svg+xml\" href=\"/favicon.svg\" />", index, StringComparison.Ordinal);
            Assert.True(faviconResponse.IsSuccessStatusCode);
            Assert.Equal("image/svg+xml", faviconResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedInit.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedQueryTokenConfiguration.StatusCode);
            Assert.True(initialized.IsSuccessStatusCode);
            Assert.True(after!.Initialized);
            Assert.Equal("initialized", after.InitializationState);
            Assert.Equal("initialized", after.InitializationOutcome);
            Assert.Equal("web", after.Client);
            Assert.True(after.PrimaryClient);
            Assert.Equal(options.Url, after.Url);
            Assert.Contains("CLI remains supported", after.CliRole);
            Assert.True(approvalsResponse.IsSuccessStatusCode);
            Assert.Empty(approvals!);
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedConfiguration.StatusCode);
            Assert.True(configurationResponse.IsSuccessStatusCode);
            Assert.True(configuration!.Status.Initialized);
            Assert.True(configuration.Permissions.Parsed);
            Assert.NotEmpty(configuration.Documents);
            Assert.NotEmpty(configuration.Concepts);
            Assert.Equal(HttpStatusCode.NotFound, missingApprovalResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Browser_cookie_jar_keeps_two_local_web_hosts_authenticated_on_different_ports()
    {
        using var firstWorkspace = new TestWorkspace();
        await using var firstApp = CreateApp(firstWorkspace.RootPath, out var firstOptions);
        await firstApp.StartAsync();
        using var secondWorkspace = new TestWorkspace();
        await using var secondApp = CreateApp(secondWorkspace.RootPath, out var secondOptions);
        await secondApp.StartAsync();

        try
        {
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler { CookieContainer = cookies };
            using var client = new HttpClient(handler);
            using var firstSession = await client.GetAsync(firstOptions.Url + "/api/session");
            using var secondSession = await client.GetAsync(secondOptions.Url + "/api/session");
            var firstSecurity = firstApp.Services.GetRequiredService<WebSessionSecurity>();
            var secondSecurity = secondApp.Services.GetRequiredService<WebSessionSecurity>();
            using var firstStatus = await client.GetAsync(firstOptions.Url + "/api/status");
            using var secondStatus = await client.GetAsync(secondOptions.Url + "/api/status");

            Assert.True(firstSession.IsSuccessStatusCode);
            Assert.True(secondSession.IsSuccessStatusCode);
            Assert.NotEqual(firstSecurity.CookieName, secondSecurity.CookieName);
            Assert.Equal(firstSecurity.Token, cookies.GetCookies(new Uri(firstOptions.Url))[firstSecurity.CookieName]?.Value);
            Assert.Equal(secondSecurity.Token, cookies.GetCookies(new Uri(secondOptions.Url))[secondSecurity.CookieName]?.Value);
            Assert.True(firstStatus.IsSuccessStatusCode);
            Assert.True(secondStatus.IsSuccessStatusCode);
        }
        finally
        {
            await secondApp.StopAsync();
            await firstApp.StopAsync();
        }
    }

    [Fact]
    public async Task Hub_negotiate_requires_session_cookie_and_rejects_query_credentials()
    {
        using var workspace = new TestWorkspace();
        using var logs = new RecordingLoggerProvider();
        await using var app = CreateApp(workspace.RootPath, out var options, logs);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            using var unauthenticatedClient = new HttpClient { BaseAddress = new Uri(options.Url) };
            var rejected = await client.PostAsync("/hubs/session/negotiate?negotiateVersion=1", null);
            using var sessionResponse = await client.GetAsync("/api/session");
            var currentToken = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var cookieRequest = new HttpRequestMessage(HttpMethod.Post, "/hubs/session/negotiate?negotiateVersion=1");
            cookieRequest.Headers.Add("Cookie", SessionCookie(sessionResponse));
            var accepted = await client.SendAsync(cookieRequest);
            var rejectedLegacyQuery = await unauthenticatedClient.GetAsync($"/api/configuration?access_token={Uri.EscapeDataString(currentToken)}");
            var rejectedNegotiateQuery = await unauthenticatedClient.PostAsync($"/hubs/session/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(currentToken)}", null);

            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.True(accepted.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedLegacyQuery.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedNegotiateQuery.StatusCode);
            Assert.NotEmpty(logs.Messages);
            Assert.DoesNotContain(logs.Messages, message => message.Contains(currentToken, StringComparison.Ordinal));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Approval_rest_path_cannot_decide_a_connection_owned_request()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var token = app.Services.GetRequiredService<WebSessionSecurity>().Token;
            var coordinator = app.Services.GetRequiredService<WebApprovalCoordinator>();
            coordinator.RegisterOwnerConnection("connection-1");
            using var scope = coordinator.BeginApprovalScope("connection-1");
            var approvalTask = coordinator.RequestApprovalAsync(CreateRequest("req-default-reject"));
            await WaitForPendingAsync(coordinator, "connection-1");
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/approvals/req-default-reject");
            request.Headers.Add(WebSessionSecurity.HeaderName, token);
            request.Content = JsonContent.Create(new { }, options: _jsonOptions);

            var response = await client.SendAsync(request);
            var hubDecision = await coordinator.SubmitDecisionAsync("req-default-reject", approved: false, detail: null, decisionConnectionId: "connection-1");
            var approval = await approvalTask;

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(hubDecision.Accepted);
            Assert.False(approval.Approved);
            Assert.Equal("Rejected in the localhost web client.", approval.Detail);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static AgentToolApprovalRequest CreateRequest(string id)
    {
        return new AgentToolApprovalRequest(
            id,
            "read",
            "shared/example.txt",
            @"C:\workspace\shared\example.txt",
            "read",
            "shared/**",
            "Needs approval.");
    }

    private static string SessionCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        return setCookie.Split(';', 2)[0];
    }

    private static async Task WaitForPendingAsync(WebApprovalCoordinator coordinator, string ownerConnectionId)
    {
        for (var i = 0; i < 20; i++)
        {
            if (coordinator.GetPending(ownerConnectionId).Count > 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Approval request was not queued.");
    }

    private static WebApplication CreateApp(string rootPath, out WebRunOptions options, ILoggerProvider? loggerProvider = null)
    {
        var port = GetFreePort();
        var arguments = new[] { "--workdir", rootPath, "--port", port.ToString(), "--model", "gpt-test" };
        options = WebRunOptions.FromArguments(arguments);
        var builder = Program.CreateBuilder(arguments, options);
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        return BuildApp(builder);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    private static WebApplication BuildApp(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
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
