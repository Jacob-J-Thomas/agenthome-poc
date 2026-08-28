using System.Diagnostics;
using System.Text;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Tests.Support;
using Xunit.Sdk;

namespace EmbodySense.Core.Persistence.Tests.Loops.EffectAttempts;

public sealed class GovernedLoopEffectAttemptCrashRecoveryTests
{
    private const string WorkerWorkspaceVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_WORKSPACE";
    private const string WorkerModeVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_MODE";
    private const string WorkerBoundaryVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_BOUNDARY";
    private const string WorkerCallbackEvidenceVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_CALLBACK_EVIDENCE";
    private const string WorkerReadyVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_READY";
    private const string WorkerResultVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_RESULT";
    private const int MaximumWorkerEvidenceCharacters = 8_192;
    private const int VstestCrashExitCode = 1;
    private const int TestHostCrashExitCode = 73;
    private static readonly DateTimeOffset _preparedAtUtc = DateTimeOffset.Parse("2026-08-12T20:00:00Z");
    private static readonly TimeSpan _workerReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _workerResultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _workerExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _workerEvidenceReadTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(
        CrashBoundary.BeforeIntentPublication,
        null,
        0,
        GovernedLoopEffectPhase.Committed,
        1)]
    [InlineData(
        CrashBoundary.AfterIntentBeforeBoundary,
        GovernedLoopEffectPhase.IntentPrepared,
        0,
        GovernedLoopEffectPhase.Committed,
        1)]
    [InlineData(
        CrashBoundary.AfterBoundaryBeforeOutcome,
        GovernedLoopEffectPhase.DispatchBoundaryReached,
        1,
        GovernedLoopEffectPhase.ReconciliationRequired,
        1)]
    [InlineData(
        CrashBoundary.AfterOutcomeBeforeCommit,
        GovernedLoopEffectPhase.OutcomeObserved,
        1,
        GovernedLoopEffectPhase.Committed,
        1)]
    public async Task External_process_loss_retains_one_safe_restart_decision_without_repeat_dispatch(
        CrashBoundary boundary,
        GovernedLoopEffectPhase? retainedPhase,
        int callbackCountAfterCrash,
        GovernedLoopEffectPhase recoveredPhase,
        int callbackCountAfterRecovery)
    {
        using var workspace = new TestWorkspace();
        var callbackEvidencePath = workspace.File("effect-dispatch-callbacks.log");
        var crashReadyPath = workspace.File("effect-crash-ready.txt");
        var crashResultPath = workspace.File("effect-crash-result.txt");
        var recoveryReadyPath = workspace.File("effect-recovery-ready.txt");
        var recoveryResultPath = workspace.File("effect-recovery-result.txt");

        var crashWorker = StartWorker(
                   workspace.RootPath,
                   "crash",
                   boundary,
                   callbackEvidencePath,
                   crashReadyPath,
                   crashResultPath);
        using (crashWorker.EvidenceCancellation)
        using (crashWorker.Process)
        {
            var crash = new Verification.CrossProcessReadinessChild(
                "crash",
                crashWorker.Process,
                crashReadyPath,
                crashResultPath,
                crashWorker.StandardOutputTask,
                crashWorker.StandardErrorTask,
                crashWorker.EvidenceCancellation);
            await Verification.CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync(
                "effect-attempt/crash",
                [crash],
                _workerReadinessTimeout);
            var evidence = await WaitForExpectedCrashAsync(crash, crashWorker, boundary);
            Assert.Equal(VstestCrashExitCode, crashWorker.Process.ExitCode);
            Assert.Contains("test run was aborted", evidence.StandardError, StringComparison.OrdinalIgnoreCase);
        }

        var paths = new WorkspacePaths(workspace.RootPath);
        var retained = ReadDurableHead(paths);
        Assert.Equal(retainedPhase, retained?.Payload.Phase);
        Assert.Equal(callbackCountAfterCrash, ReadCallbackCount(callbackEvidencePath));
        if (boundary == CrashBoundary.BeforeIntentPublication)
        {
            Assert.False(Directory.Exists(paths.GovernedLoopEffectAttemptsPath));
        }

        var recoveryWorker = StartWorker(
                   workspace.RootPath,
                   "recover",
                   boundary,
                   callbackEvidencePath,
                   recoveryReadyPath,
                   recoveryResultPath);
        using (recoveryWorker.EvidenceCancellation)
        using (recoveryWorker.Process)
        {
            var recovery = new Verification.CrossProcessReadinessChild(
                "recover",
                recoveryWorker.Process,
                recoveryReadyPath,
                recoveryResultPath,
                recoveryWorker.StandardOutputTask,
                recoveryWorker.StandardErrorTask,
                recoveryWorker.EvidenceCancellation);
            await Verification.CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync(
                "effect-attempt/recover",
                [recovery],
                _workerReadinessTimeout);
            await Verification.CrossProcessReadinessDiagnostics.WaitForChildrenCompletedAsync(
                "effect-attempt/recover",
                "durable recovery result",
                [recovery],
                _workerResultTimeout,
                Verification.CrossProcessReadinessDiagnostics.CoverageChildTeardownTimeout);
            await AssertProcessSucceededAsync(recoveryWorker);
        }

        Assert.Equal(recoveredPhase.ToString(), await File.ReadAllTextAsync(recoveryResultPath));
        Assert.Equal(recoveredPhase, ReadDurableHead(paths)?.Payload.Phase);
        Assert.Equal(callbackCountAfterRecovery, ReadCallbackCount(callbackEvidencePath));
        Assert.All(
            Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.json"),
            path => Assert.DoesNotContain("secret", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task External_process_loss_fails_fast_after_invalid_marker_worker_exit()
    {
        using var workspace = new TestWorkspace();
        var callbackEvidencePath = workspace.File("invalid-marker-callbacks.log");
        var readyPath = workspace.File("invalid-marker-ready.txt");
        var resultPath = workspace.File("invalid-marker-result.txt");
        var worker = StartWorker(
            workspace.RootPath,
            "invalid-marker",
            CrashBoundary.BeforeIntentPublication,
            callbackEvidencePath,
            readyPath,
            resultPath);
        using (worker.EvidenceCancellation)
        using (worker.Process)
        {
            var child = new Verification.CrossProcessReadinessChild(
                "invalid-marker",
                worker.Process,
                readyPath,
                resultPath,
                worker.StandardOutputTask,
                worker.StandardErrorTask,
                worker.EvidenceCancellation);
            await Verification.CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync(
                "effect-attempt/invalid-marker",
                [child],
                _workerReadinessTimeout);

            var wait = Stopwatch.StartNew();
            var failure = await Assert.ThrowsAsync<FailException>(() => WaitForExpectedCrashAsync(child, worker, CrashBoundary.BeforeIntentPublication));

            Assert.True(
                wait.Elapsed < TimeSpan.FromSeconds(10),
                $"The invalid-marker worker failure took {wait.Elapsed.TotalSeconds:0.###} seconds instead of failing after its terminal exit.");
            Assert.Contains("Last readable marker: `invalid-marker`", failure.Message, StringComparison.Ordinal);
            Assert.Equal(VstestCrashExitCode, worker.Process.ExitCode);
        }
    }

    [Fact]
    public async Task Cross_process_effect_attempt_worker()
    {
        var workspaceRoot = Environment.GetEnvironmentVariable(WorkerWorkspaceVariable);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            Assert.True(UsesExpectedTerminationVstestHost("crash"));
            Assert.True(UsesExpectedTerminationVstestHost("invalid-marker"));
            Assert.False(UsesExpectedTerminationVstestHost("recover"));
            Assert.Throws<ArgumentOutOfRangeException>(() => UsesExpectedTerminationVstestHost("invalid"));
            return;
        }

        var mode = Environment.GetEnvironmentVariable(WorkerModeVariable)
            ?? throw new InvalidOperationException("The effect-attempt worker mode is required.");
        var boundary = Enum.Parse<CrashBoundary>(
            Environment.GetEnvironmentVariable(WorkerBoundaryVariable)
                ?? throw new InvalidOperationException("The effect-attempt crash boundary is required."));
        var callbackEvidencePath = Environment.GetEnvironmentVariable(WorkerCallbackEvidenceVariable)
            ?? throw new InvalidOperationException("The effect-attempt callback evidence path is required.");
        var readyPath = Environment.GetEnvironmentVariable(WorkerReadyVariable)
            ?? throw new InvalidOperationException("The effect-attempt worker readiness path is required.");
        var resultPath = Environment.GetEnvironmentVariable(WorkerResultVariable)
            ?? throw new InvalidOperationException("The effect-attempt recovery result path is required.");
        var adapter = new CrashRestartProtocolAdapter(
            new GovernedLoopEffectAttemptStore(new WorkspacePaths(workspaceRoot)),
            callbackEvidencePath,
            resultPath);

        await File.WriteAllTextAsync(readyPath, mode);
        if (string.Equals(mode, "invalid-marker", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(resultPath, "invalid-marker");
            Environment.Exit(TestHostCrashExitCode);
            throw new InvalidOperationException("The invalid-marker worker did not terminate.");
        }

        if (string.Equals(mode, "crash", StringComparison.Ordinal))
        {
            await adapter.CrashAsync(boundary);
            throw new InvalidOperationException("The requested effect-attempt crash boundary did not terminate the worker.");
        }
        if (!string.Equals(mode, "recover", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The effect-attempt worker mode is invalid.");
        }

        var recovered = await adapter.RecoverAsync();
        await File.WriteAllTextAsync(resultPath, recovered.Payload.Phase.ToString());
    }

    private static WorkerProcessCapture StartWorker(
        string workspaceRoot,
        string mode,
        CrashBoundary boundary,
        string callbackEvidencePath,
        string readyPath,
        string resultPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        AddWorkerVstestArguments(startInfo, mode);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[WorkerWorkspaceVariable] = workspaceRoot;
        startInfo.Environment[WorkerModeVariable] = mode;
        startInfo.Environment[WorkerBoundaryVariable] = boundary.ToString();
        startInfo.Environment[WorkerCallbackEvidenceVariable] = callbackEvidencePath;
        startInfo.Environment[WorkerReadyVariable] = readyPath;
        startInfo.Environment[WorkerResultVariable] = resultPath;
        var process = Verification.CrossProcessProcessOwnership.Start(startInfo);
        var evidenceCancellation = new CancellationTokenSource();
        return new WorkerProcessCapture(
            process,
            evidenceCancellation,
            ReadWorkerStreamAsync(process.ReadStandardOutputToEndAsync(evidenceCancellation.Token)),
            ReadWorkerStreamAsync(process.ReadStandardErrorToEndAsync(evidenceCancellation.Token)));
    }

    private static void AddWorkerVstestArguments(ProcessStartInfo startInfo, string mode)
    {
        var assemblyPath = typeof(GovernedLoopEffectAttemptCrashRecoveryTests).Assembly.Location;
        var testName = $"{typeof(GovernedLoopEffectAttemptCrashRecoveryTests).FullName}.{nameof(Cross_process_effect_attempt_worker)}";
        if (UsesExpectedTerminationVstestHost(mode))
        {
            Verification.CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(startInfo, assemblyPath, testName);
        }
        else
        {
            Verification.CoverageChildProcessAssembly.AddVstestArguments(startInfo, assemblyPath, testName);
        }
    }

    private static bool UsesExpectedTerminationVstestHost(string mode)
        => mode switch
        {
            "crash" or "invalid-marker" => true,
            "recover" => false,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The effect-attempt worker mode is not supported.")
        };

    private static async Task<(string StandardOutput, string StandardError)> WaitForExpectedCrashAsync(
        Verification.CrossProcessReadinessChild worker,
        WorkerProcessCapture capture,
        CrashBoundary boundary)
    {
        var resultWait = Stopwatch.StartNew();
        var expectedBoundary = boundary.ToString();
        string? observedBoundary = null;
        while (true)
        {
            if (File.Exists(worker.ResultPath))
            {
                var observed = await TryReadWorkerResultMarkerAsync(worker.ResultPath);
                if (observed is not null)
                {
                    observedBoundary = observed;
                    if (string.Equals(expectedBoundary, observedBoundary, StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }

            if (worker.Process.HasExited)
            {
                var observed = await TryReadWorkerResultMarkerAsync(worker.ResultPath);
                if (observed is not null)
                {
                    observedBoundary = observed;
                    if (string.Equals(expectedBoundary, observedBoundary, StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                var evidence = await ReadWorkerEvidenceAsync(capture);
                var lastReadableMarker = observedBoundary ?? "<unreadable>";
                Assert.Fail($"Effect-attempt crash worker exited without publishing the `{boundary}` durable boundary result. Last readable marker: `{lastReadableMarker}`. {evidence}");
            }
            if (resultWait.Elapsed >= _workerResultTimeout)
            {
                var evidence = await StopAndReadWorkerEvidenceAsync(worker, capture);
                var observed = observedBoundary ?? "<unreadable>";
                Assert.Fail($"Effect-attempt crash worker did not publish the `{boundary}` durable boundary result within {_workerResultTimeout.TotalSeconds:0} seconds. Last readable marker: `{observed}`. {evidence}");
            }

            await Task.Delay(10);
        }

        try
        {
            await worker.Process.WaitForExitAsync().WaitAsync(_workerExitTimeout);
        }
        catch (TimeoutException)
        {
            var evidence = await StopAndReadWorkerEvidenceAsync(worker, capture);
            Assert.Fail($"Effect-attempt crash worker published the `{boundary}` durable boundary result but did not exit within {_workerExitTimeout.TotalSeconds:0} seconds. {evidence}");
        }

        return await ReadWorkerEvidenceAsync(capture);
    }

    private static async Task<string?> TryReadWorkerResultMarkerAsync(string resultPath)
    {
        try
        {
            return await File.ReadAllTextAsync(resultPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task AssertProcessSucceededAsync(WorkerProcessCapture capture)
    {
        var output = await ReadWorkerEvidenceAsync(capture);
        Assert.True(
            capture.Process.ExitCode == 0,
            $"Effect-attempt recovery worker exited with `{capture.Process.ExitCode}`.{Environment.NewLine}{output.StandardError}{Environment.NewLine}{output.StandardOutput}");
    }

    private static async Task<(string StandardOutput, string StandardError)> ReadWorkerEvidenceAsync(WorkerProcessCapture capture)
    {
        var drainTask = Task.WhenAll(capture.StandardOutputTask, capture.StandardErrorTask);
        try
        {
            await drainTask.WaitAsync(_workerEvidenceReadTimeout);
        }
        catch (TimeoutException)
        {
            capture.EvidenceCancellation.Cancel();
            await drainTask.WaitAsync(_workerEvidenceReadTimeout);
        }

        return (capture.StandardOutputTask.Result, capture.StandardErrorTask.Result);
    }

    private static async Task<string> ReadWorkerStreamAsync(Task<string> streamTask)
    {
        try
        {
            return BoundWorkerEvidence(await streamTask);
        }
        catch (OperationCanceledException)
        {
            return "<timed-out>";
        }
        catch (IOException)
        {
            return "<unavailable>";
        }
        catch (ObjectDisposedException)
        {
            return "<unavailable>";
        }
    }

    private static async Task<string> StopAndReadWorkerEvidenceAsync(
        Verification.CrossProcessReadinessChild worker,
        WorkerProcessCapture capture)
    {
        try
        {
            worker.Ownership.TerminateProcessTree();
        }
        catch (InvalidOperationException) when (worker.Process.HasExited)
        {
        }

        try
        {
            await worker.Process.WaitForExitAsync().WaitAsync(_workerEvidenceReadTimeout);
        }
        catch (TimeoutException)
        {
        }

        var evidence = await ReadWorkerEvidenceAsync(capture);
        var state = worker.Process.HasExited ? $"exited exit={worker.Process.ExitCode}" : "still-running exit=<unavailable>";
        return $"pid={worker.Process.Id} state={state} ready={File.Exists(worker.ReadyPath)} result={File.Exists(worker.ResultPath)} stdout={evidence.StandardOutput} stderr={evidence.StandardError}";
    }

    private static string BoundWorkerEvidence(string evidence)
        => evidence.Length <= MaximumWorkerEvidenceCharacters
            ? evidence
            : "<truncated>" + evidence[^MaximumWorkerEvidenceCharacters..];

    private static GovernedLoopEffectAttempt? ReadDurableHead(WorkspacePaths paths)
    {
        if (!Directory.Exists(paths.GovernedLoopEffectAttemptsPath))
        {
            return null;
        }

        var headPath = Assert.Single(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head"));
        var contentHash = File.ReadAllText(headPath);
        var recordPath = Assert.Single(
            Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, $"*.{contentHash}.json"));
        Assert.True(
            GovernedLoopEffectAttemptRecordCodec.TryDecode(File.ReadAllBytes(recordPath), out var attempt, out var failure),
            failure);
        return attempt;
    }

    private static int ReadCallbackCount(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var receipts = File.ReadAllLines(path);
        Assert.All(receipts, receipt => Assert.Equal("dispatch-boundary-crossed", receipt));
        return receipts.Length;
    }

    private static GovernedLoopEffectAttempt Prepare()
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/effects/probe", out var capabilityId, out var capabilityError), capabilityError?.Message);
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var capabilityVersion, out var versionError), versionError?.Message);
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + Hash('1'), out var descriptorHash, out var hashError), hashError?.Message);
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out var providerError), providerError?.Message);
        var revision = GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", Hash('a'));
        var binding = GovernedLoopExecutionBinding.Create(1, "run-1", revision, 1);
        return GovernedLoopEffectAttemptContract.Prepare(
            binding,
            "action-1",
            1,
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!),
            new CapabilityImplementationIdentity(providerId!, "effects/probe"),
            "probe/observe",
            Hash('b'),
            "effect-1",
            "effect-operation-1",
            1,
            Hash('2'),
            Hash('3'),
            Hash('4'),
            Hash('5'),
            "probe-before",
            _preparedAtUtc);
    }

    private static string Hash(char value) => new(value, 64);

    public enum CrashBoundary
    {
        BeforeIntentPublication,
        AfterIntentBeforeBoundary,
        AfterBoundaryBeforeOutcome,
        AfterOutcomeBeforeCommit,
    }

    private sealed record WorkerProcessCapture(
        Verification.CrossProcessProcess Process,
        CancellationTokenSource EvidenceCancellation,
        Task<string> StandardOutputTask,
        Task<string> StandardErrorTask);

    private sealed class CrashRestartProtocolAdapter(
        IGovernedLoopEffectAttemptStore store,
        string callbackEvidencePath,
        string crashResultPath)
    {
        public async Task CrashAsync(CrashBoundary boundary)
        {
            if (boundary == CrashBoundary.BeforeIntentPublication)
            {
                await TerminateWorkerAsync(boundary);
            }

            var begun = RequireOwner(await store.BeginAsync(Prepare()));
            using var lease = begun.Lease!;
            var current = begun.Attempt!;
            if (boundary == CrashBoundary.AfterIntentBeforeBoundary)
            {
                await TerminateWorkerAsync(boundary);
            }

            current = await AttachAuthorityAsync(current, lease);
            current = await CrossBoundaryAsync(current, lease);
            AppendCallbackEvidence();
            if (boundary == CrashBoundary.AfterBoundaryBeforeOutcome)
            {
                await TerminateWorkerAsync(boundary);
            }

            current = await ObserveOutcomeAsync(current, lease);
            if (boundary == CrashBoundary.AfterOutcomeBeforeCommit)
            {
                await TerminateWorkerAsync(boundary);
            }

            _ = await CommitAsync(current, lease);
        }

        public async Task<GovernedLoopEffectAttempt> RecoverAsync()
        {
            var begun = RequireOwner(await store.BeginAsync(Prepare()));
            using var lease = begun.Lease!;
            var current = begun.Attempt!;
            if (current.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared)
            {
                if (current.DispatchAuthorityEvidenceHash is null)
                {
                    current = await AttachAuthorityAsync(current, lease);
                }
                current = await CrossBoundaryAsync(current, lease);
                AppendCallbackEvidence();
                current = await ObserveOutcomeAsync(current, lease);
                return await CommitAsync(current, lease);
            }
            if (current.Payload.Phase == GovernedLoopEffectPhase.DispatchBoundaryReached)
            {
                var reconciliation = GovernedLoopEffectAttemptContract.Advance(
                    current,
                    GovernedLoopEffectPhase.ReconciliationRequired,
                    GovernedLoopEffectOutcome.OutcomeUnknown,
                    GovernedLoopEffectEvidenceStatus.Incomplete,
                    null,
                    null,
                    _preparedAtUtc.AddSeconds(3));
                return RequireStored(await store.CompareExchangeAsync(current.ContentHash, reconciliation, lease));
            }
            if (current.Payload.Phase == GovernedLoopEffectPhase.OutcomeObserved)
            {
                return await CommitAsync(current, lease);
            }
            if (current.Payload.Phase is GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.ReconciliationRequired)
            {
                return current;
            }

            throw new InvalidOperationException($"The retained phase `{current.Payload.Phase}` is not recoverable by this protocol adapter.");
        }

        private async Task<GovernedLoopEffectAttempt> AttachAuthorityAsync(
            GovernedLoopEffectAttempt current,
            IGovernedLoopEffectAttemptLease lease)
        {
            var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(
                current,
                Hash('8'),
                _preparedAtUtc.AddSeconds(1));
            return RequireStored(await store.CompareExchangeAsync(current.ContentHash, authorized, lease));
        }

        private async Task<GovernedLoopEffectAttempt> CrossBoundaryAsync(
            GovernedLoopEffectAttempt current,
            IGovernedLoopEffectAttemptLease lease)
        {
            var crossed = GovernedLoopEffectAttemptContract.Advance(
                current,
                GovernedLoopEffectPhase.DispatchBoundaryReached,
                GovernedLoopEffectOutcome.OutcomeUnknown,
                GovernedLoopEffectEvidenceStatus.Pending,
                null,
                null,
                _preparedAtUtc.AddSeconds(2));
            return RequireStored(await store.CompareExchangeAsync(current.ContentHash, crossed, lease));
        }

        private async Task<GovernedLoopEffectAttempt> ObserveOutcomeAsync(
            GovernedLoopEffectAttempt current,
            IGovernedLoopEffectAttemptLease lease)
        {
            var observed = GovernedLoopEffectAttemptContract.Advance(
                current,
                GovernedLoopEffectPhase.OutcomeObserved,
                GovernedLoopEffectOutcome.Succeeded,
                GovernedLoopEffectEvidenceStatus.Complete,
                "probe-outcome",
                "probe-after",
                _preparedAtUtc.AddSeconds(3));
            return RequireStored(await store.CompareExchangeAsync(current.ContentHash, observed, lease));
        }

        private async Task<GovernedLoopEffectAttempt> CommitAsync(
            GovernedLoopEffectAttempt current,
            IGovernedLoopEffectAttemptLease lease)
        {
            var committed = GovernedLoopEffectAttemptContract.Advance(
                current,
                GovernedLoopEffectPhase.Committed,
                current.Payload.Outcome,
                current.Payload.EvidenceStatus,
                current.Payload.OutcomeEvidenceId,
                current.AfterEvidenceId,
                _preparedAtUtc.AddSeconds(4));
            return RequireStored(await store.CompareExchangeAsync(current.ContentHash, committed, lease));
        }

        private void AppendCallbackEvidence()
        {
            using var stream = new FileStream(callbackEvidencePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            var receipt = Encoding.UTF8.GetBytes("dispatch-boundary-crossed" + Environment.NewLine);
            stream.Write(receipt);
            stream.Flush(flushToDisk: true);
        }

        private static GovernedLoopEffectAttemptStoreResult RequireOwner(GovernedLoopEffectAttemptStoreResult result)
        {
            Assert.Contains(result.Status, new[] { GovernedLoopEffectAttemptStoreStatus.Created, GovernedLoopEffectAttemptStoreStatus.Replayed });
            Assert.NotNull(result.Attempt);
            Assert.NotNull(result.Lease);
            return result;
        }

        private static GovernedLoopEffectAttempt RequireStored(GovernedLoopEffectAttemptStoreResult result)
        {
            Assert.Contains(result.Status, new[] { GovernedLoopEffectAttemptStoreStatus.Created, GovernedLoopEffectAttemptStoreStatus.Replayed });
            return Assert.IsType<GovernedLoopEffectAttempt>(result.Attempt);
        }

        private async Task TerminateWorkerAsync(CrashBoundary boundary)
        {
            await File.WriteAllTextAsync(crashResultPath, boundary.ToString());
            Environment.Exit(TestHostCrashExitCode);
            throw new InvalidOperationException("The test host did not terminate.");
        }
    }
}
