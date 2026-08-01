using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Clients.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

public sealed class IsolatedCapabilityExecutableHostTests
{
    [Fact]
    public void Default_host_and_secret_requiring_artifact_fail_closed()
    {
        using var defaultHost = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog());
        using var configuredHost = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver());

        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, defaultHost.CheckAvailability(CapabilityClientTestData.Manifest()).Status);
        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, configuredHost.CheckAvailability(CapabilityClientTestData.Manifest(secrets: true)).Status);
        Assert.Throws<PlatformNotSupportedException>(() => DenyingCapabilityProcessIsolationBoundary.Instance.StartIsolated(new ProcessStartInfo(), CapabilityClientTestData.Manifest(), null!));
    }

    [Fact]
    public async Task Configured_boundary_cannot_claim_available_execution_without_a_host_supported_lease_binding()
    {
        using var artifact = PrepareArtifact();
        var boundary = new TestCapabilityProcessIsolationBoundary();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var invocation = new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, "{}", "platform-binding");

        var availability = host.CheckAvailability(invocation.Manifest);
        var result = await host.InvokeAsync(invocation);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(CapabilityExecutableAvailabilityStatus.Available, availability.Status);
            Assert.Equal(CapabilityExecutableInvocationStatus.Succeeded, result.Status);
            Assert.Equal(1, boundary.Starts);
            return;
        }

        Assert.Equal(CapabilityExecutableAvailabilityStatus.Unavailable, availability.Status);
        Assert.Contains("handle-bound", availability.Detail, StringComparison.Ordinal);
        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, result.Status);
        Assert.Equal(0, boundary.Starts);
    }

    [Fact]
    public async Task Caller_supplied_artifact_root_is_unavailable_without_a_proved_resolver()
    {
        using var artifact = PrepareArtifact();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary());

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, "{}", "unproved-root"));

        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, result.Status);
    }

    [Fact]
    public void Host_redacts_platform_availability_diagnostics()
    {
        var boundary = new TestCapabilityProcessIsolationBoundary { Availability = new(CapabilityExecutableAvailabilityStatus.Unavailable, "password=hunter2 C:\\private\\secret.txt") };
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, new TestCapabilityExecutableArtifactResolver());

        var availability = host.CheckAvailability(CapabilityClientTestData.Manifest());

        Assert.DoesNotContain("hunter2", availability.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("private", availability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("echo", CapabilityExecutableInvocationStatus.Succeeded)]
    [InlineData("malformed", CapabilityExecutableInvocationStatus.MalformedResult)]
    [InlineData("crash", CapabilityExecutableInvocationStatus.Crashed)]
    [InlineData("oversize", CapabilityExecutableInvocationStatus.OutputLimitExceeded)]
    public async Task External_process_results_are_bounded_structured_and_redacted(string behavior, CapabilityExecutableInvocationStatus expected)
    {
        using var artifact = PrepareArtifact();
        var audit = new RecordingCapabilityAuditLog();
        using var host = new IsolatedCapabilityExecutableHost(audit, new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint, behavior, outputBytes: behavior == "oversize" ? 1_024 : 16_384);

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(manifest, artifact.RootPath, "{\"hello\":\"world\"}", "invoke-1"));

        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain("hunter2", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, item => Assert.DoesNotContain("artifactRoot", item.Metadata.Keys));
        if (expected == CapabilityExecutableInvocationStatus.Succeeded)
        {
            Assert.Equal("{\"hello\":\"world\"}", result.OutputJson);
        }
    }

    [Fact]
    public async Task Hang_times_out_and_process_tree_is_terminated()
    {
        using var artifact = PrepareArtifact();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint, "hang", milliseconds: 100);

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(manifest, artifact.RootPath, "{}", "timeout-1"));

        Assert.Equal(CapabilityExecutableInvocationStatus.TimedOut, result.Status);
        Assert.True(result.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Caller_cancellation_terminates_running_process()
    {
        using var artifact = PrepareArtifact();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint, "hang", milliseconds: 10_000);

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(manifest, artifact.RootPath, "{}", "cancel-1"), cancellation.Token);

        Assert.Equal(CapabilityExecutableInvocationStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Declared_concurrency_bound_admits_only_one_process_at_a_time()
    {
        using var artifact = PrepareArtifact();
        var boundary = new TestCapabilityProcessIsolationBoundary();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint, "hang", milliseconds: 10_000);
        var first = host.InvokeAsync(new CapabilityExecutableInvocation(manifest, artifact.RootPath, "{}", "concurrency-1"), firstCancellation.Token);
        await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var secondCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var second = await host.InvokeAsync(new CapabilityExecutableInvocation(manifest, artifact.RootPath, "{}", "concurrency-2"), secondCancellation.Token);
        var firstResult = await first;

        Assert.Equal(CapabilityExecutableInvocationStatus.Cancelled, second.Status);
        Assert.Equal(CapabilityExecutableInvocationStatus.Cancelled, firstResult.Status);
        Assert.Equal(1, boundary.Starts);
    }

    [Fact]
    public async Task Environment_is_cleared_and_working_directory_is_exact_artifact_root()
    {
        using var artifact = PrepareArtifact();
        var boundary = new TestCapabilityProcessIsolationBoundary();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var environment = await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint, "environment"), artifact.RootPath, "{}", "env-1"));
        var workingRoot = await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint, "working-root"), artifact.RootPath, "{}", "root-1"));

        var names = Assert.IsType<string[]>(JsonSerializer.Deserialize<string[]>(environment.OutputJson!));
        Assert.Equal(["EMBODYSENSE_CAPABILITY_ID", "EMBODYSENSE_CAPABILITY_VERSION"], names);
        Assert.Equal(Path.TrimEndingDirectorySeparator(artifact.RootPath), boundary.LastWorkingDirectory);
        Assert.Equal("[path]", JsonSerializer.Deserialize<string>(workingRoot.OutputJson!));
    }

    [Fact]
    public async Task Path_escape_and_malformed_input_never_start_process()
    {
        using var artifact = PrepareArtifact();
        var boundary = new TestCapabilityProcessIsolationBoundary();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var malformed = await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, "not-json", "bad-input"));
        var oversized = await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, JsonSerializer.Serialize(new string('x', 16 * 1024 * 1024)), "oversized-input"));
        var escapeManifest = CapabilityClientTestData.Manifest(artifact.EntryPoint) with { EntryPoint = "../outside.exe" };
        var escaped = await host.InvokeAsync(new CapabilityExecutableInvocation(escapeManifest, artifact.RootPath, "{}", "escape"));

        Assert.Equal(CapabilityExecutableInvocationStatus.Invalid, malformed.Status);
        Assert.Equal(CapabilityExecutableInvocationStatus.Invalid, oversized.Status);
        Assert.Equal(CapabilityExecutableInvocationStatus.Invalid, escaped.Status);
        Assert.Equal(0, boundary.Starts);
    }

    [Fact]
    public async Task Invalid_unavailable_and_missing_artifacts_fail_while_legacy_caller_root_is_ignored()
    {
        using var artifact = PrepareArtifact();
        var boundary = new TestCapabilityProcessIsolationBoundary { Availability = new(CapabilityExecutableAvailabilityStatus.Unavailable, "Unavailable for test.") };
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var invalidManifest = CapabilityClientTestData.Manifest(artifact.EntryPoint) with { SchemaVersion = 2 };

        Assert.Equal(CapabilityExecutableAvailabilityStatus.Incompatible, host.CheckAvailability(invalidManifest).Status);
        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, (await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, "{}", "unavailable"))).Status);
        boundary.Availability = new(CapabilityExecutableAvailabilityStatus.Available, "Available for test.");
        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, (await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest("missing.exe"), artifact.RootPath, "{}", "missing"))).Status);
        Assert.Equal(CapabilityExecutableInvocationStatus.Succeeded, (await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), "invalid\0root", "{}", "ignored-caller-root"))).Status);
        Assert.Equal(1, boundary.Starts);
    }

    [Fact]
    public async Task Platform_isolation_start_failure_is_structured_and_redacted()
    {
        using var artifact = PrepareArtifact();
        var boundary = new TestCapabilityProcessIsolationBoundary { StartException = new PlatformNotSupportedException("private platform detail") };
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, new TestCapabilityExecutableArtifactResolver(artifact.RootPath));

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, "{}", "platform-failure"));

        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, result.Status);
        Assert.DoesNotContain("private", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Executable_path_substitution_between_resolution_and_start_is_blocked_by_lease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var artifact = PrepareArtifact();
        var boundary = new SubstitutingCapabilityProcessIsolationBoundary();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, new TestCapabilityExecutableArtifactResolver(artifact.RootPath));

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, "{}", "substitution-race"));

        Assert.True(boundary.SubstitutionBlocked);
        Assert.Equal(CapabilityExecutableInvocationStatus.Succeeded, result.Status);
    }

    private static PreparedArtifact PrepareArtifact()
    {
        var repositoryRoot = FindRepositoryRoot();
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var source = FindCancellationHostOutput(repositoryRoot, outputDirectory);
        var workspace = new TestWorkspace();
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(workspace.RootPath, Path.GetFileName(file)));
        }
        var entryPoint = OperatingSystem.IsWindows() ? "EmbodySense.CancellationHost.exe" : "EmbodySense.CancellationHost";
        return new PreparedArtifact(workspace, entryPoint);
    }

    private static string FindCancellationHostOutput(string repositoryRoot, DirectoryInfo outputDirectory)
    {
        var redirectedOutput = Path.Combine(outputDirectory.Parent!.Parent!.FullName, "EmbodySense.CancellationHost", outputDirectory.Name);
        if (File.Exists(Path.Combine(redirectedOutput, "EmbodySense.CancellationHost.dll")))
        {
            return redirectedOutput;
        }
        var configuration = outputDirectory.Parent.Name;
        var targetFramework = outputDirectory.Name;
        return Path.Combine(repositoryRoot, "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class PreparedArtifact : IDisposable
    {
        private readonly TestWorkspace _workspace;

        internal PreparedArtifact(TestWorkspace workspace, string entryPoint)
        {
            _workspace = workspace;
            EntryPoint = entryPoint;
        }

        internal string RootPath => _workspace.RootPath;
        internal string EntryPoint { get; }
        public void Dispose() => _workspace.Dispose();
    }
}
