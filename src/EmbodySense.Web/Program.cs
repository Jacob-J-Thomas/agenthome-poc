using Microsoft.AspNetCore.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Web.Hubs;
using EmbodySense.Web.Services;

namespace EmbodySense.Web;

public static class Program
{
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

    public static WebApplicationBuilder CreateBuilder(string[] args, WebRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = ResolveContentRoot(), ApplicationName = typeof(Program).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        builder.WebHost.UseUrls(options.Url);
        ConfigureServices(builder.Services, options);
        return builder;
    }

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
        services.AddSingleton<WebSessionSecurity>();
        services.AddSingleton<IWebClientNotifier, SignalRWebClientNotifier>();
        services.AddSingleton<WebApprovalCoordinator>();
        services.AddSingleton<WebAgentRuntimeHost>();
        services.AddSingleton<IWebLoopRuntimeInvoker>(provider => provider.GetRequiredService<WebAgentRuntimeHost>());
        services.AddSingleton(_ => new LoopAuthoringFacade(options.WorkingDirectory));
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (context, next) =>
        {
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self' ws://127.0.0.1:* ws://localhost:* ws://[::1]:*; base-uri 'none'; frame-ancestors 'none'; object-src 'none'";
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

    public static string ResolveContentRoot()
    {
        return ResolveContentRoot(AppContext.BaseDirectory, Directory.GetCurrentDirectory());
    }

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

    public static void PrintHelp(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("""
            EmbodySense Web UI

            usage:
              embodysense-web [--workdir path] [--host 127.0.0.1] [--port 4378]

            options:
              --workdir path     Workspace root for governed tools, permissions, and audit.
              --host host        Local bind host: 127.0.0.1, localhost, or ::1.
              --port port        Local bind port. Defaults to 4378.
              --model model       Model name passed to the configured inference surface.
              --codex-path path   Codex executable path for app-server inferencing.
              --sandbox mode      Codex app-server sandbox for the inert runtime directory.
            """);
    }
}
