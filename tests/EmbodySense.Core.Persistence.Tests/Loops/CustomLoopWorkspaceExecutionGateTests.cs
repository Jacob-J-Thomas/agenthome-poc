using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class CustomLoopWorkspaceExecutionGateTests
{
    private static readonly string _firstHash = new('1', CustomLoopLimits.Sha256HexCharacters);
    private static readonly string _secondHash = new('2', CustomLoopLimits.Sha256HexCharacters);

    [Fact]
    public async Task Canonical_workspace_gate_never_waits_and_releases_after_execution()
    {
        using var workspace = new TestWorkspace();
        var firstPaths = new WorkspacePaths(workspace.RootPath);
        var canonicalAlias = new WorkspacePaths(Path.Combine(workspace.RootPath, "."));
        await using var first = new CustomLoopWorkspaceExecutionGate(firstPaths);
        await using var second = new CustomLoopWorkspaceExecutionGate(canonicalAlias);

        var acquired = first.TryAcquire("invoke-one", _firstHash);
        var workspaceBusy = second.TryAcquire("invoke-two", _secondHash);
        var sameOperation = second.TryAcquire("invoke-one", _firstHash);
        var changedOperation = second.TryAcquire("invoke-one", _secondHash);

        Assert.Equal(CustomLoopExecutionLeaseStatus.Acquired, acquired.Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.WorkspaceBusy, workspaceBusy.Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationInProgress, sameOperation.Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationConflict, changedOperation.Status);

        var busyReservation = second.TryReserveWorkspaceBusyOutcome("invoke-two", _secondHash);
        Assert.Equal(CustomLoopExecutionLeaseStatus.BusyOutcomeReserved, busyReservation.Status);
        Assert.NotNull(busyReservation.Lease);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationInProgress, first.TryReserveWorkspaceBusyOutcome("invoke-two", _secondHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationConflict, first.TryReserveWorkspaceBusyOutcome("invoke-two", _firstHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationInProgress, second.TryReserveWorkspaceBusyOutcome("invoke-one", _firstHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationConflict, second.TryReserveWorkspaceBusyOutcome("invoke-one", _secondHash).Status);
        acquired.Lease!.Dispose();
        acquired.Lease.Dispose();
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationInProgress, first.TryAcquire("invoke-two", _secondHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.OperationConflict, first.TryAcquire("invoke-two", _firstHash).Status);
        busyReservation.Lease!.Dispose();
        busyReservation.Lease.Dispose();
        using var next = second.TryAcquire("invoke-two", _secondHash).Lease;
        Assert.NotNull(next);
        next.Dispose();
        Assert.Equal(CustomLoopExecutionLeaseStatus.WorkspaceAvailable, second.TryReserveWorkspaceBusyOutcome("invoke-three", _firstHash).Status);
        Assert.Throws<ArgumentException>(() => first.TryAcquire("INVALID", _firstHash));
        Assert.Throws<ArgumentException>(() => first.TryAcquire("invoke-three", "bad-hash"));
        Assert.Throws<ArgumentException>(() => first.TryReserveWorkspaceBusyOutcome("INVALID", _firstHash));
        Assert.Throws<ArgumentException>(() => first.TryReserveWorkspaceBusyOutcome("invoke-three", "bad-hash"));
    }

    [Fact]
    public async Task Gate_holds_file_ownership_until_all_host_references_are_disposed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = new CustomLoopWorkspaceExecutionGate(paths);
        var second = new CustomLoopWorkspaceExecutionGate(paths);

        Assert.True(File.Exists(paths.CustomLoopHostLockPath));

        await first.DisposeAsync();
        await second.DisposeAsync();
        await second.DisposeAsync();

        using var ownershipAfterRelease = new WindowsFileLock(paths.CustomLoopHostLockPath);
    }

    [Fact]
    public async Task Gate_reports_unavailable_host_without_blocking_construction_when_another_process_owns_the_lock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopRunsPath);
        using var ownership = new WindowsFileLock(paths.CustomLoopHostLockPath);

        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);

        Assert.Equal(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, gate.TryAcquire("invoke-one", _firstHash).Status);
        Assert.Equal(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, gate.TryReserveWorkspaceBusyOutcome("invoke-one", _firstHash).Status);
    }

    [Fact]
    public async Task Gate_retries_host_ownership_after_the_external_owner_releases_the_lock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.LoopRunsPath);
        using var ownership = new WindowsFileLock(paths.CustomLoopHostLockPath);
        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);

        Assert.Equal(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, gate.TryAcquire("invoke-one", _firstHash).Status);
        ownership.Dispose();
        using var acquired = gate.TryAcquire("invoke-one", _firstHash).Lease;

        Assert.NotNull(acquired);
    }

    [Fact]
    public async Task Gate_can_relinquish_its_host_reference_and_reacquire_later()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
        var active = gate.TryAcquire("invoke-one", _firstHash);
        Assert.NotNull(active.Lease);

        gate.RelinquishWorkspaceHost();
        active.Lease.Dispose();
        using (var externalOwnership = new WindowsFileLock(paths.CustomLoopHostLockPath))
        {
            Assert.NotNull(externalOwnership);
        }

        using var reacquired = gate.TryAcquire("invoke-two", _secondHash).Lease;
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task Shared_process_runtimes_route_cancellation_to_the_registered_attempt_owner()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var owner = new CustomLoopWorkspaceExecutionGate(paths);
        await using var requester = new CustomLoopWorkspaceExecutionGate(paths);
        using var cancellation = new CancellationTokenSource();
        using var registration = owner.RegisterActiveAttempt("run-shared-owner", cancellation);
        var confirmation = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(10);
            }

            Assert.True(registration.TryConfirmProviderInterruption(cancellation.Token));
        });

        var result = await requester.RequestCancellationAsync("run-shared-owner", "cancel-shared-owner");
        await confirmation;

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(CustomLoopAttemptCancellationStatus.ProviderInterruptionConfirmed, result.Status);
        Assert.StartsWith("owner-", result.OwnerId);
        Assert.Equal(Environment.ProcessId, result.OwnerProcessId);
    }

    [Fact]
    public async Task Attempt_completion_race_reports_delivery_without_claiming_interruption()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
        using var cancellation = new CancellationTokenSource();
        var registration = gate.RegisterActiveAttempt("run-completion-race", cancellation);
        var request = gate.RequestCancellationAsync("run-completion-race", "cancel-completion-race");
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(10);
        }

        registration.Dispose();
        var result = await request;

        Assert.Equal(CustomLoopAttemptCancellationStatus.SignalDelivered, result.Status);
        Assert.Equal(CustomLoopAttemptCancellationStatus.NoActiveAttempt, (await gate.RequestCancellationAsync("run-completion-race", "cancel-after-completion")).Status);
    }

    [Fact]
    public async Task Unresponsive_attempt_returns_signal_delivery_at_the_bounded_timeout()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
        using var cancellation = new CancellationTokenSource();
        using var registration = gate.RegisterActiveAttempt("run-signal-timeout", cancellation);
        var startedAt = Stopwatch.GetTimestamp();

        var result = await gate.RequestCancellationAsync("run-signal-timeout", "cancel-signal-timeout");
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(CustomLoopAttemptCancellationStatus.SignalDelivered, result.Status);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Blocking_cancellation_callback_cannot_stall_the_remote_broker()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var owner = new CustomLoopWorkspaceExecutionGate(paths);
        await using var requester = new CustomLoopWorkspaceExecutionGate(paths);
        requester.RelinquishWorkspaceHost();
        using var cancellation = new CancellationTokenSource();
        using var callbackEntered = new ManualResetEventSlim();
        using var callbackExited = new ManualResetEventSlim();
        using var callbackRelease = new ManualResetEventSlim();
        using var requestCompleted = new ManualResetEventSlim();
        using var callback = cancellation.Token.Register(() =>
        {
            callbackEntered.Set();
            callbackRelease.Wait();
            callbackExited.Set();
        });
        using var registration = owner.RegisterActiveAttempt("run-blocking-callback", cancellation);

        try
        {
            var request = requester.RequestCancellationAsync("run-blocking-callback", "cancel-blocking-callback");
            _ = request.ContinueWith(
                static (_, state) => ((ManualResetEventSlim)state!).Set(),
                requestCompleted,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(15)), "The routed cancellation callback was not entered within the bounded wait.");
            Assert.True(requestCompleted.Wait(TimeSpan.FromSeconds(15)), "The remote broker did not complete while the cancellation callback remained blocked.");
            var result = await request;

            Assert.Equal(CustomLoopAttemptCancellationStatus.SignalDelivered, result.Status);
            Assert.False(callbackExited.IsSet);
        }
        finally
        {
            callbackRelease.Set();
        }
    }

    [Fact]
    public async Task Already_cancelled_provider_token_is_not_reported_as_delivered_by_a_later_routed_signal()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var registration = gate.RegisterActiveAttempt("run-unrelated-cancellation", cancellation);
        var request = gate.RequestCancellationAsync("run-unrelated-cancellation", "cancel-after-unrelated");

        Assert.False(registration.TryConfirmProviderInterruption(cancellation.Token));
        registration.Dispose();
        var result = await request;

        Assert.Equal(CustomLoopAttemptCancellationStatus.OwnerUnavailable, result.Status);
    }

    [Fact]
    public async Task Competing_cancellation_prevents_routed_provider_interruption_confirmation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
        using var competing = new CancellationTokenSource();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(competing.Token);
        var registration = gate.RegisterActiveAttempt("run-competing-cancellation", cancellation, competing.Token);
        var request = gate.RequestCancellationAsync("run-competing-cancellation", "cancel-competing");
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(10);
        }

        competing.Cancel();
        Assert.False(registration.TryConfirmProviderInterruption(cancellation.Token));
        registration.Dispose();
        var result = await request;

        Assert.Equal(CustomLoopAttemptCancellationStatus.OwnerUnavailable, result.Status);
    }

    [Fact]
    public async Task Windows_descriptor_reader_allows_atomic_owner_generation_replacement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var owner = new CustomLoopWorkspaceExecutionGate(paths);
        await using var reader = new FileStream(paths.CustomLoopCancellationOwnerPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var replacement = paths.CustomLoopCancellationOwnerPath + ".replacement";
        await File.WriteAllTextAsync(replacement, "{}");

        File.Replace(replacement, paths.CustomLoopCancellationOwnerPath, destinationBackupFileName: null);

        Assert.Equal("{}", await File.ReadAllTextAsync(paths.CustomLoopCancellationOwnerPath));
    }

    [Fact]
    public async Task Tampered_owner_capability_is_rejected_by_the_live_host()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var owner = new CustomLoopWorkspaceExecutionGate(paths);
        using var cancellation = new CancellationTokenSource();
        using var registration = owner.RegisterActiveAttempt("run-invalid-capability", cancellation);
        var descriptor = JsonNode.Parse(await File.ReadAllBytesAsync(paths.CustomLoopCancellationOwnerPath))!.AsObject();
        descriptor["secret"] = Convert.ToBase64String(new byte[32]);
        await File.WriteAllTextAsync(paths.CustomLoopCancellationOwnerPath, descriptor.ToJsonString());
        await using var requester = new CustomLoopWorkspaceExecutionGate(paths);
        requester.RelinquishWorkspaceHost();

        var result = await requester.RequestCancellationAsync("run-invalid-capability", "cancel-invalid-capability");

        Assert.Equal(CustomLoopAttemptCancellationStatus.Invalid, result.Status);
        Assert.False(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task Null_authentication_tag_is_rejected_without_terminating_the_broker()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var owner = new CustomLoopWorkspaceExecutionGate(paths);
        using var cancellation = new CancellationTokenSource();
        using var registration = owner.RegisterActiveAttempt("run-null-authentication", cancellation);
        var descriptor = JsonNode.Parse(await File.ReadAllBytesAsync(paths.CustomLoopCancellationOwnerPath))!.AsObject();
        var invalid = await ExchangePipeFrameAsync(
            descriptor,
            new JsonObject
            {
                ["schemaVersion"] = 1,
                ["ownerId"] = descriptor["ownerId"]!.GetValue<string>(),
                ["runId"] = "run-null-authentication",
                ["operationId"] = "cancel-null-authentication",
                ["authenticationTag"] = null
            });
        await using var requester = new CustomLoopWorkspaceExecutionGate(paths);
        requester.RelinquishWorkspaceHost();
        var confirmation = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(10);
            }

            Assert.True(registration.TryConfirmProviderInterruption(cancellation.Token));
        });

        var result = await requester.RequestCancellationAsync("run-null-authentication", "cancel-after-null-authentication");
        await confirmation;

        Assert.Equal((int)CustomLoopAttemptCancellationStatus.Invalid, invalid["status"]!.GetValue<int>());
        Assert.Equal(CustomLoopAttemptCancellationStatus.ProviderInterruptionConfirmed, result.Status);
    }

    [Fact]
    public async Task Unix_owner_capability_is_published_with_owner_only_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var owner = new CustomLoopWorkspaceExecutionGate(paths);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.CustomLoopCancellationOwnerPath));
    }

    [Fact]
    public async Task Incomplete_client_frame_is_abandoned_before_a_later_authenticated_cancel()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await using var owner = new CustomLoopWorkspaceExecutionGate(paths);
        using var cancellation = new CancellationTokenSource();
        using var registration = owner.RegisterActiveAttempt("run-incomplete-client", cancellation);
        var descriptor = JsonNode.Parse(await File.ReadAllBytesAsync(paths.CustomLoopCancellationOwnerPath))!.AsObject();
        using var incomplete = new NamedPipeClientStream(".", descriptor["pipeName"]!.GetValue<string>(), PipeDirection.InOut, PipeOptions.Asynchronous);
        await incomplete.ConnectAsync();
        await incomplete.WriteAsync(new byte[] { 1 });
        await incomplete.FlushAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var confirmation = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(10);
            }

            Assert.True(registration.TryConfirmProviderInterruption(cancellation.Token));
        });

        var result = await owner.RequestCancellationAsync("run-incomplete-client", "cancel-after-incomplete-client");
        await confirmation;

        Assert.Equal(CustomLoopAttemptCancellationStatus.ProviderInterruptionConfirmed, result.Status);
    }

    [Fact]
    public async Task Child_process_owner_authenticates_and_confirms_provider_interruption()
    {
        using var workspace = new TestWorkspace();
        using var process = StartCancellationHost(workspace.RootPath, "run-cross-process-owner");
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await using var requester = new CustomLoopWorkspaceExecutionGate(new WorkspacePaths(workspace.RootPath));

            var result = await requester.RequestCancellationAsync("run-cross-process-owner", "cancel-cross-process-owner");

            Assert.Equal(CustomLoopAttemptCancellationStatus.ProviderInterruptionConfirmed, result.Status);
            Assert.StartsWith("owner-", result.OwnerId);
            Assert.Equal(process.Id, result.OwnerProcessId);
            Assert.Equal("interrupted", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Public_lifecycle_boundary_routes_cross_process_cancellation_and_completes_an_honest_receipt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        const string RunId = "run-public-cross-process";
        using var process = StartCancellationHost(workspace.RootPath, RunId);
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var runStore = new CustomLoopRunStore(paths);
            var operationStore = new CustomLoopControlOperationStore(paths);
            var running = RunningRun(RunId);
            await PersistRunningRunAsync(runStore, running);
            await using var requester = new CustomLoopWorkspaceExecutionGate(paths);
            var service = new CustomLoopLifecycleService(
                runStore,
                operationStore,
                new UnusedResumeExecutor(),
                new AvailableModel(),
                new RoutingCancellationSignal(requester),
                new AuditLog(paths),
                requester);
            var request = new CustomLoopCancelRequest(RunId, running.LifecycleVersion, "cancel-public-cross-process", AuditSchema.Actors.Web);

            var result = await service.CancelAsync(request);
            var replay = await service.CancelAsync(request);

            Assert.Equal(CustomLoopControlStatus.CancelRequested, result.Status);
            Assert.Contains("confirmed", result.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"process {process.Id}", result.Detail, StringComparison.Ordinal);
            Assert.Equal(result.Status, replay.Status);
            Assert.Contains("replayed", replay.Detail, StringComparison.OrdinalIgnoreCase);
            var receipt = Assert.IsType<CustomLoopControlOperation>(await operationStore.GetAsync(request.OperationId));
            Assert.Equal(CustomLoopControlOperationState.Complete, receipt.State);
            Assert.Equal(CustomLoopControlStatus.CancelRequested, receipt.Outcome);
            Assert.Equal("interrupted", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Owner_exit_is_unavailable_and_a_new_generation_accepts_the_same_retry()
    {
        using var workspace = new TestWorkspace();
        using var process = StartCancellationHost(workspace.RootPath, "run-owner-restart");
        Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await using var requester = new CustomLoopWorkspaceExecutionGate(new WorkspacePaths(workspace.RootPath));
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();

        var unavailable = await requester.RequestCancellationAsync("run-owner-restart", "cancel-owner-restart");

        Assert.Equal(CustomLoopAttemptCancellationStatus.OwnerUnavailable, unavailable.Status);
        await using var replacement = new CustomLoopWorkspaceExecutionGate(new WorkspacePaths(workspace.RootPath));
        using var cancellation = new CancellationTokenSource();
        using var registration = replacement.RegisterActiveAttempt("run-owner-restart", cancellation);
        var confirmation = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(10);
            }

            Assert.True(registration.TryConfirmProviderInterruption(cancellation.Token));
        });
        var retried = await requester.RequestCancellationAsync("run-owner-restart", "cancel-owner-restart");
        await confirmation;

        Assert.Equal(CustomLoopAttemptCancellationStatus.ProviderInterruptionConfirmed, retried.Status);
    }

    [Fact]
    public void Gate_rejects_a_reparse_point_run_root_when_the_platform_allows_links()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LoopRunsPath)!);
        var target = workspace.File("reparse-run-target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(paths.LoopRunsPath, target);
        }
        catch (Exception linkException) when (linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => new CustomLoopWorkspaceExecutionGate(paths));
        Assert.Contains("reparse points or junctions", exception.Message, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root could not be located from the test output directory.");
    }

    private static Process StartCancellationHost(string workspaceRoot, string runId)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var hostAssembly = Path.Combine(FindRepositoryRoot(), "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework, "EmbodySense.CancellationHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Cancellation host assembly was not built at `{hostAssembly}`.");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add(workspaceRoot);
        startInfo.ArgumentList.Add(runId);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The cancellation owner process could not be started.");
    }

    private static async Task<JsonObject> ExchangePipeFrameAsync(JsonObject descriptor, JsonObject request)
    {
        using var client = new NamedPipeClientStream(".", descriptor["pipeName"]!.GetValue<string>(), PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var payload = Encoding.UTF8.GetBytes(request.ToJsonString());
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await client.WriteAsync(length);
        await client.WriteAsync(payload);
        await client.FlushAsync();
        await client.ReadExactlyAsync(length).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var responseLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        var response = new byte[responseLength];
        await client.ReadExactlyAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        return JsonNode.Parse(response)!.AsObject();
    }

    private static CustomLoopRunRecord RunningRun(string runId)
    {
        var now = DateTimeOffset.Parse("2026-07-26T12:00:00+00:00");
        var definition = CustomLoopDefinitionContentHash.Apply(CustomLoopDefinition.CreateSeed("loop-public-cancellation", "role-workspace", "step-only", "create-loop-public-cancellation", now) with { ContentHash = string.Empty });
        var events = new CustomLoopRunEvent[]
        {
            new(1, $"admitted-{runId}", now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null),
            new(2, $"admission-audit-{runId}", now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null),
            new(3, $"running-{runId}", now.AddSeconds(1), CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered Running.", [], null, null, null, null, null, null, null, null, null, null)
        };
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            runId,
            definition.Id,
            events.Length,
            CustomLoopRunStatus.Running,
            now,
            now.AddSeconds(1),
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            $"admit-{runId}",
            AuditSchema.Actors.Web,
            string.Empty,
            definition,
            "prompt",
            null,
            CustomLoopContextSnapshot.CreateEmpty(now),
            new CustomLoopExecutionClock(0, now.AddSeconds(1)),
            CustomLoopRunCheckpoint.Start(),
            events,
            null,
            null,
            null)
        {
            CapabilityAdmission = TestCapabilityAdmissionFactory.Create(definition.CapabilityRequirements, now)
        };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static async Task PersistRunningRunAsync(CustomLoopRunStore store, CustomLoopRunRecord running)
    {
        var admitted = running with
        {
            LifecycleVersion = 1,
            Status = CustomLoopRunStatus.Admitted,
            UpdatedAtUtc = running.CreatedAtUtc,
            ExecutionClock = CustomLoopExecutionClock.NotStarted(),
            Events = [running.Events[0]]
        };
        var audited = admitted with
        {
            LifecycleVersion = 2,
            Events = [.. running.Events[..2]]
        };

        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(admitted)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(audited, admitted.LifecycleVersion)).Status);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(running, audited.LifecycleVersion)).Status);
    }

    private sealed class RoutingCancellationSignal(CustomLoopWorkspaceExecutionGate gate) : ICustomLoopExecutionCancellationSignal
    {
        public IDisposable? TryRegisterActiveRun(string runId) => null;

        public void CancelActiveAttempt(string runId) => throw new NotSupportedException();

        public Task<CustomLoopAttemptCancellationResult> RequestActiveAttemptCancellationAsync(string runId, string operationId, CancellationToken cancellationToken = default)
        {
            return gate.RequestCancellationAsync(runId, operationId, cancellationToken);
        }
    }

    private sealed class UnusedResumeExecutor : ICustomLoopResumeExecutor
    {
        public Task<CustomLoopOrderedRunResult> ResumeAsync(CustomLoopResumeExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AvailableModel : ICustomLoopModelAvailability
    {
        public Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
