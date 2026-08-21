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

namespace EmbodySense.Core.Persistence.Tests.Loops.EffectAttempts;

public sealed class GovernedLoopEffectAttemptCrashRecoveryTests
{
    private const string WorkerWorkspaceVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_WORKSPACE";
    private const string WorkerModeVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_MODE";
    private const string WorkerBoundaryVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_BOUNDARY";
    private const string WorkerCallbackEvidenceVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_CALLBACK_EVIDENCE";
    private const string WorkerResultVariable = "EMBODYSENSE_EFFECT_ATTEMPT_WORKER_RESULT";
    private const int VstestCrashExitCode = 1;
    private const int TestHostCrashExitCode = 73;
    private static readonly DateTimeOffset _preparedAtUtc = DateTimeOffset.Parse("2026-08-12T20:00:00Z");

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
        var recoveryResultPath = workspace.File("effect-recovery-result.txt");

        using (var crashWorker = StartWorker(
                   workspace.RootPath,
                   "crash",
                   boundary,
                   callbackEvidencePath,
                   recoveryResultPath))
        {
            var crash = await WaitForExitAsync(crashWorker);
            Assert.Equal(VstestCrashExitCode, crashWorker.ExitCode);
            Assert.Contains("test run was aborted", crash.StandardError, StringComparison.OrdinalIgnoreCase);
        }

        var paths = new WorkspacePaths(workspace.RootPath);
        var retained = ReadDurableHead(paths);
        Assert.Equal(retainedPhase, retained?.Payload.Phase);
        Assert.Equal(callbackCountAfterCrash, ReadCallbackCount(callbackEvidencePath));
        if (boundary == CrashBoundary.BeforeIntentPublication)
        {
            Assert.False(Directory.Exists(paths.GovernedLoopEffectAttemptsPath));
        }

        using (var recoveryWorker = StartWorker(
                   workspace.RootPath,
                   "recover",
                   boundary,
                   callbackEvidencePath,
                   recoveryResultPath))
        {
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
    public async Task Cross_process_effect_attempt_worker()
    {
        var workspaceRoot = Environment.GetEnvironmentVariable(WorkerWorkspaceVariable);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            Assert.True(UsesExpectedTerminationVstestHost("crash"));
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
        var resultPath = Environment.GetEnvironmentVariable(WorkerResultVariable)
            ?? throw new InvalidOperationException("The effect-attempt recovery result path is required.");
        var adapter = new CrashRestartProtocolAdapter(
            new GovernedLoopEffectAttemptStore(new WorkspacePaths(workspaceRoot)),
            callbackEvidencePath);

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

    private static Process StartWorker(
        string workspaceRoot,
        string mode,
        CrashBoundary boundary,
        string callbackEvidencePath,
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
        startInfo.Environment[WorkerResultVariable] = resultPath;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The effect-attempt crash/restart test worker did not start.");
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
            "crash" => true,
            "recover" => false,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The effect-attempt worker mode is not supported.")
        };

    private static async Task<(string StandardOutput, string StandardError)> WaitForExitAsync(Process process)
    {
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        return (await outputTask, await errorTask);
    }

    private static async Task AssertProcessSucceededAsync(Process process)
    {
        var output = await WaitForExitAsync(process);
        Assert.True(
            process.ExitCode == 0,
            $"Effect-attempt recovery worker exited with `{process.ExitCode}`.{Environment.NewLine}{output.StandardError}{Environment.NewLine}{output.StandardOutput}");
    }

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

    private sealed class CrashRestartProtocolAdapter(
        IGovernedLoopEffectAttemptStore store,
        string callbackEvidencePath)
    {
        public async Task CrashAsync(CrashBoundary boundary)
        {
            if (boundary == CrashBoundary.BeforeIntentPublication)
            {
                TerminateWorker();
            }

            var begun = RequireOwner(await store.BeginAsync(Prepare()));
            using var lease = begun.Lease!;
            var current = begun.Attempt!;
            if (boundary == CrashBoundary.AfterIntentBeforeBoundary)
            {
                TerminateWorker();
            }

            current = await AttachAuthorityAsync(current, lease);
            current = await CrossBoundaryAsync(current, lease);
            AppendCallbackEvidence();
            if (boundary == CrashBoundary.AfterBoundaryBeforeOutcome)
            {
                TerminateWorker();
            }

            current = await ObserveOutcomeAsync(current, lease);
            if (boundary == CrashBoundary.AfterOutcomeBeforeCommit)
            {
                TerminateWorker();
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

        private static void TerminateWorker()
        {
            Environment.Exit(TestHostCrashExitCode);
            throw new InvalidOperationException("The test host did not terminate.");
        }
    }
}
