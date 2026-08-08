using EmbodySense.Web.Models;
using Microsoft.AspNetCore.Authentication;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Web.Hubs;
using EmbodySense.Web.Services;

namespace EmbodySense.Web;

/// <summary>
/// Composes and starts the localhost EmbodySense Web host.
/// </summary>
public static class Program
{
    /// <summary>
    /// Parses Web options, prints help without starting a server, or runs the configured host until shutdown.
    /// </summary>
    /// <param name="args">The supported Web command-line options.</param>
    /// <returns>Zero after help output or an orderly server shutdown.</returns>
    /// <exception cref="ArgumentException">The required model or another host, port, sandbox, or option value is invalid.</exception>
    public static async Task<int> Main(string[] args)
    {
        var options = WebRunOptions.FromArguments(args);
        if (options.PrintHelp)
        {
            PrintHelp(Console.Out);
            return 0;
        }

        var builder = CreateBuilder(args, options);
        await using var app = builder.Build();
        ConfigurePipeline(app);

        Console.WriteLine($"EmbodySense Web UI listening at {options.Url}");
        Console.WriteLine($"Workspace: {options.WorkingDirectory}");
        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Creates a Web application builder with the resolved static-content root, local URL, logging, and services.
    /// </summary>
    /// <param name="args">The original host arguments supplied to ASP.NET.</param>
    /// <param name="options">The validated EmbodySense Web options.</param>
    /// <returns>An unbuilt application builder.</returns>
    public static WebApplicationBuilder CreateBuilder(string[] args, WebRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = ResolveContentRoot(), ApplicationName = Assembly.GetExecutingAssembly().GetName().Name });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        // ASP.NET's information-level request logs include raw query strings before application middleware runs.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.WebHost.UseUrls(options.Url);
        ConfigureServices(builder.Services, options);
        return builder;
    }

    /// <summary>
    /// Registers strict JSON controllers, bounded SignalR, local-session security, and singleton runtime services.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="options">The validated workspace and runtime options captured by singleton services.</param>
    /// <remarks>
    /// JSON rejects unmapped members and numeric enum values. SignalR limits invocation payload size and
    /// parallel calls. The runtime host, approval coordinator, authoring facade, and session token are
    /// process singletons and are disposed by the application container when applicable.
    /// </remarks>
    public static void ConfigureServices(IServiceCollection services, WebRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        });
        services.AddSignalR().AddHubOptions<WebSessionHub>(options =>
        {
            options.MaximumReceiveMessageSize = LoopRunTransportLimits.MaxSignalRInvocationMessageUtf8Bytes;
            options.MaximumParallelInvocationsPerClient = 2;
        });
        services.AddAuthentication(WebSessionAuthenticationDefaults.Scheme).AddScheme<AuthenticationSchemeOptions, WebSessionAuthenticationHandler>(WebSessionAuthenticationDefaults.Scheme, _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(WebAuthPolicies.LocalSession, policy =>
            {
                policy.AuthenticationSchemes.Add(WebSessionAuthenticationDefaults.Scheme);
                policy.RequireAuthenticatedUser();
            });
        });
        services.AddSingleton(options);
        services.AddSingleton(_ => WebSessionSecurity.CreateForWorkspace(options.WorkingDirectory, options.Port));
        services.AddSingleton<IWebClientNotifier, SignalRWebClientNotifier>();
        services.AddSingleton<IAgentRuntimeConversationPublicationObserver, WebConversationPublicationObserver>();
        services.AddSingleton<WebApprovalCoordinator>();
        services.AddSingleton(provider => new WebAgentRuntimeHost(
            options,
            provider.GetRequiredService<WebApprovalCoordinator>(),
            WorkspaceInitializer.ForWeb(),
            provider.GetRequiredService<IAgentRuntimeConversationPublicationObserver>()));
        services.AddSingleton<IWebLoopRuntimeInvoker>(provider => provider.GetRequiredService<WebAgentRuntimeHost>());
        services.AddSingleton(_ => new LoopAuthoringFacade(options.WorkingDirectory));
        services.AddSingleton<ILoopReceiptRetentionFacade>(_ => new LoopReceiptRetentionFacade(options.WorkingDirectory));
    }

    /// <summary>
    /// Configures security headers, static files, authentication, authorization, controllers, and the session hub.
    /// </summary>
    /// <param name="app">The built application.</param>
    /// <remarks>
    /// Authentication runs before authorization. HTTP controllers own their endpoint policies, while
    /// <c>/hubs/session</c> explicitly requires the local-session policy.
    /// </remarks>
    public static void ConfigurePipeline(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (context, next) =>
        {
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapHub<WebSessionHub>("/hubs/session").RequireAuthorization(WebAuthPolicies.LocalSession);
    }

    /// <summary>
    /// Resolves the static-content root from the application base directory and current directory.
    /// </summary>
    /// <returns>The first recognized Web project or fallback directory.</returns>
    public static string ResolveContentRoot()
    {
        return ResolveContentRoot(AppContext.BaseDirectory, Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// Resolves a directory containing the Web static entry point across published, project, and repository layouts.
    /// </summary>
    /// <param name="baseDirectory">The application base directory from which ancestor discovery starts.</param>
    /// <param name="fallbackDirectory">The directory used for repository fallback and final fallback.</param>
    /// <returns>
    /// The base directory when it contains static assets; otherwise the nearest project or repository-style
    /// <c>src/EmbodySense.Web</c> directory; otherwise the fallback directory.
    /// </returns>
    public static string ResolveContentRoot(string baseDirectory, string fallbackDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);

        if (HasStaticWebEntryPoint(baseDirectory))
        {
            return baseDirectory;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbodySense.Web.csproj")) && HasStaticWebEntryPoint(directory.FullName))
            {
                return directory.FullName;
            }

            var sourceProjectPath = Path.Combine(directory.FullName, "src", "EmbodySense.Web");
            if (File.Exists(Path.Combine(sourceProjectPath, "EmbodySense.Web.csproj")) && HasStaticWebEntryPoint(sourceProjectPath))
            {
                return sourceProjectPath;
            }

            directory = directory.Parent;
        }

        var fallbackSourceProjectPath = Path.Combine(fallbackDirectory, "src", "EmbodySense.Web");
        if (File.Exists(Path.Combine(fallbackSourceProjectPath, "EmbodySense.Web.csproj")) && HasStaticWebEntryPoint(fallbackSourceProjectPath))
        {
            return fallbackSourceProjectPath;
        }

        return fallbackDirectory;
    }

    private static bool HasStaticWebEntryPoint(string directory)
    {
        return Directory.Exists(Path.Combine(directory, "wwwroot")) && File.Exists(Path.Combine(directory, "wwwroot", "index.html"));
    }

    /// <summary>
    /// Writes usage for the implemented localhost Web host options.
    /// </summary>
    /// <param name="writer">The output writer.</param>
    public static void PrintHelp(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("""
            EmbodySense Web UI

            usage:
              embodysense-web --model model [--workdir path] [--host 127.0.0.1] [--port 4378]

            options:
              --workdir path     Workspace root for governed tools, permissions, and audit.
              --host host        Local bind host: 127.0.0.1, localhost, or ::1.
              --port port        Local bind port. Defaults to 4378.
              --model model      Required model name passed to the configured inference surface.
              --codex-path path   Codex executable path for app-server inferencing.
              --sandbox mode      Codex app-server sandbox for the inert runtime directory.
            """);
    }
}
