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
    public async Task Configured_boundary_obeys_the_host_supported_lease_binding_contract()
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
        var boundary = new TestCapabilityProcessIsolationBoundary();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary);

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, "{}", "unproved-root"));

        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, result.Status);
        Assert.Equal(0, boundary.Starts);
    }

    [WindowsFact]
    public async Task Resolver_and_lease_failures_remain_structured_without_starting_a_process()
    {
        using var artifact = PrepareArtifact();
        var boundary = new TestCapabilityProcessIsolationBoundary();
        var resolver = new TestCapabilityExecutableArtifactResolver { Resolution = new(CapabilityExecutableAvailabilityStatus.Available, null, "Lease was omitted.") };
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, resolver);
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint);

        var missingLease = new CapabilityExecutableInvocation(
            manifest, artifact.RootPath, "{}", "missing-lease");
        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, (await host.InvokeAsync(missingLease)).Status);

        resolver.ResolveException = new IOException("private resolver detail");
        var resolverFailure = new CapabilityExecutableInvocation(
            manifest, artifact.RootPath, "{}", "resolver-failure");
        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, (await host.InvokeAsync(resolverFailure)).Status);

        resolver.ResolveException = null;
        var mismatchedLease = new TestCapabilityExecutableArtifactLease(
            artifact.RootPath, Path.Combine(artifact.RootPath, artifact.EntryPoint), EmbodySense.Core.Common.Capabilities.CapabilityIntegrityDigest.Compute("wrong"u8), 0);
        resolver.Resolution = new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Available, mismatchedLease, "Mismatched lease.");
        var mismatchedInvocation = new CapabilityExecutableInvocation(
            manifest, artifact.RootPath, "{}", "mismatched-lease");
        Assert.Equal(CapabilityExecutableInvocationStatus.Invalid, (await host.InvokeAsync(mismatchedInvocation)).Status);
        Assert.Equal(0, boundary.Starts);
    }

    [WindowsFact]
    public async Task Lifecycle_change_at_final_launch_fence_prevents_process_start()
    {
        using var artifact = PrepareArtifact();
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint);
        var lease = new TestCapabilityExecutableArtifactLease(artifact.RootPath, Path.Combine(artifact.RootPath, artifact.EntryPoint), manifest.Checksum, 1, launchAllowed: false);
        var resolver = new TestCapabilityExecutableArtifactResolver { Resolution = new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Available, lease, "Resolved before lifecycle transition.") };
        var boundary = new TestCapabilityProcessIsolationBoundary();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, resolver);

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(manifest, artifact.RootPath, "{}", "lifecycle-changed-before-launch", 1));

        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, result.Status);
        Assert.Equal(0, boundary.Starts);
    }

    [WindowsFact]
    public async Task Cancellation_while_acquiring_final_launch_fence_is_reported_as_cancelled()
    {
        using var artifact = PrepareArtifact();
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint);
        var lease = new TestCapabilityExecutableArtifactLease(artifact.RootPath, Path.Combine(artifact.RootPath, artifact.EntryPoint), manifest.Checksum, 1, waitForLaunchCancellation: true);
        var resolver = new TestCapabilityExecutableArtifactResolver { Resolution = new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Available, lease, "Resolved before launch cancellation.") };
        var boundary = new TestCapabilityProcessIsolationBoundary();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, resolver);
        using var cancellation = new CancellationTokenSource();
        var invocation = host.InvokeAsync(new CapabilityExecutableInvocation(manifest, artifact.RootPath, "{}", "cancelled-at-launch-fence", 1), cancellation.Token);

        await lease.LaunchFenceAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CapabilityExecutableInvocationStatus.Cancelled, result.Status);
        Assert.Equal(0, boundary.Starts);
    }

    [WindowsFact]
    public async Task Resolver_cancellation_and_boundary_failure_are_safe_terminal_results()
    {
        using var artifact = PrepareArtifact();
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var cancelledHost = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver { ReturnCancellation = true });

        var cancelledInvocation = new CapabilityExecutableInvocation(
            manifest, artifact.RootPath, "{}", "resolver-cancelled");
        var cancelled = await cancelledHost.InvokeAsync(cancelledInvocation, cancellation.Token);

        Assert.Equal(CapabilityExecutableInvocationStatus.Cancelled, cancelled.Status);

        var failingBoundary = new TestCapabilityProcessIsolationBoundary { AvailabilityException = new IOException("private boundary detail") };
        using var failingHost = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), failingBoundary, new TestCapabilityExecutableArtifactResolver());
        var boundaryFailure = new CapabilityExecutableInvocation(
            manifest, artifact.RootPath, "{}", "boundary-failure");
        var unavailable = await failingHost.InvokeAsync(boundaryFailure);

        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, unavailable.Status);
        Assert.DoesNotContain("private", unavailable.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task Invalid_lease_root_fails_closed_before_process_start()
    {
        using var artifact = PrepareArtifact();
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint);
        var invalidRootLease = new TestCapabilityExecutableArtifactLease(
            artifact.RootPath, Path.Combine(artifact.RootPath, artifact.EntryPoint), manifest.Checksum, 0, "invalid\0root");
        var invalidRootResolver = new TestCapabilityExecutableArtifactResolver
        {
            Resolution = new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Available, invalidRootLease, "Malformed server lease root.")
        };
        using var invalidRootHost = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), invalidRootResolver);
        var invalidRootInvocation = new CapabilityExecutableInvocation(
            manifest, artifact.RootPath, "{}", "invalid-lease-root");

        Assert.Equal(CapabilityExecutableInvocationStatus.Invalid, (await invalidRootHost.InvokeAsync(invalidRootInvocation)).Status);
    }

    [WindowsFact]
    public async Task Stderr_overflow_terminates_the_process_without_returning_its_output()
    {
        using var artifact = PrepareArtifact();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var invocation = new CapabilityExecutableInvocation(
            CapabilityClientTestData.Manifest(artifact.EntryPoint, "stderr-oversize", outputBytes: 1_024), artifact.RootPath, "{}", "stderr-overflow");

        var result = await host.InvokeAsync(invocation);

        Assert.Equal(CapabilityExecutableInvocationStatus.OutputLimitExceeded, result.Status);
        Assert.DoesNotContain(new string('x', 64), result.Diagnostic, StringComparison.Ordinal);
    }

    [WindowsTheory]
    [InlineData("token=alpha, TOKEN : bravo; token=charlie", "alpha|bravo|charlie", "[redacted]")]
    [InlineData("secret: delta password = echo", "delta|echo", "[redacted]")]
    [InlineData("api_key=foxtrot api-key: golf", "foxtrot|golf", "[redacted]")]
    [InlineData("authorization=hotel Bearer india bearer juliet", "hotel|india|juliet", "[redacted]")]
    [InlineData("C:\\private\\alpha.txt D:\\secrets\\bravo.txt", "C:\\private\\alpha.txt|D:\\secrets\\bravo.txt", "[path]")]
    [InlineData("/var/private/alpha /opt/secrets/bravo", "/var/private/alpha|/opt/secrets/bravo", "[path]")]
    [InlineData("ToKeN=kilogram; C:\\private\\lima.txt /var/private/mike Bearer november", "kilogram|C:\\private\\lima.txt|/var/private/mike|november", "[redacted]")]
    public async Task Process_output_redacts_each_sensitive_diagnostic_family(string privateOutput, string privateFragments, string expectedMarker)
    {
        using var artifact = PrepareArtifact();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var invocation = new CapabilityExecutableInvocation(
            CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, JsonSerializer.Serialize(privateOutput), "redacted-output");

        var result = await host.InvokeAsync(invocation);

        Assert.Equal(CapabilityExecutableInvocationStatus.Succeeded, result.Status);
        Assert.Contains(expectedMarker, result.OutputJson, StringComparison.Ordinal);
        foreach (var privateFragment in privateFragments.Split('|'))
        {
            Assert.DoesNotContain(privateFragment, result.OutputJson, StringComparison.Ordinal);
        }
    }

    [WindowsFact]
    public async Task Process_output_is_bounded_after_redaction_without_changing_the_execution_outcome()
    {
        using var artifact = PrepareArtifact();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var privateOutput = new string('x', 2_048);
        var invocation = new CapabilityExecutableInvocation(
            CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, JsonSerializer.Serialize(privateOutput), "bounded-output");

        var result = await host.InvokeAsync(invocation);

        Assert.Equal(CapabilityExecutableInvocationStatus.Succeeded, result.Status);
        Assert.Equal(1_024, result.OutputJson!.Length);
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

    [WindowsTheory]
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

    [WindowsFact]
    public async Task Hang_times_out_and_process_tree_is_terminated()
    {
        using var artifact = PrepareArtifact();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint, "hang", milliseconds: 100);

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(manifest, artifact.RootPath, "{}", "timeout-1"));

        Assert.Equal(CapabilityExecutableInvocationStatus.TimedOut, result.Status);
        Assert.True(result.Duration < TimeSpan.FromSeconds(10));
    }

    [WindowsFact]
    public async Task Caller_cancellation_terminates_running_process()
    {
        using var artifact = PrepareArtifact();
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), new TestCapabilityProcessIsolationBoundary(), new TestCapabilityExecutableArtifactResolver(artifact.RootPath));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var manifest = CapabilityClientTestData.Manifest(artifact.EntryPoint, "hang", milliseconds: 10_000);

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(manifest, artifact.RootPath, "{}", "cancel-1"), cancellation.Token);

        Assert.Equal(CapabilityExecutableInvocationStatus.Cancelled, result.Status);
    }

    [WindowsFact]
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

    [WindowsFact]
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

    [WindowsFact]
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

    [WindowsFact]
    public async Task Platform_isolation_start_failure_is_structured_and_redacted()
    {
        using var artifact = PrepareArtifact();
        var boundary = new TestCapabilityProcessIsolationBoundary { StartException = new PlatformNotSupportedException("private platform detail") };
        using var host = new IsolatedCapabilityExecutableHost(new RecordingCapabilityAuditLog(), boundary, new TestCapabilityExecutableArtifactResolver(artifact.RootPath));

        var result = await host.InvokeAsync(new CapabilityExecutableInvocation(CapabilityClientTestData.Manifest(artifact.EntryPoint), artifact.RootPath, "{}", "platform-failure"));

        Assert.Equal(CapabilityExecutableInvocationStatus.Unavailable, result.Status);
        Assert.DoesNotContain("private", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task Executable_path_substitution_between_resolution_and_start_is_blocked_by_lease()
    {
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
