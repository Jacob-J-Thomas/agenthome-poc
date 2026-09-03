using System.Diagnostics;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Reconciliation;

public sealed class GovernedLoopEffectReconciliationProbeProcessTests
{
    private const string WorkerWorkspaceVariable = "EMBODYSENSE_RECONCILIATION_PROBE_WORKSPACE";
    private const string WorkerModeVariable = "EMBODYSENSE_RECONCILIATION_PROBE_MODE";
    private const string WorkerBoundaryVariable = "EMBODYSENSE_RECONCILIATION_PROBE_BOUNDARY";
    private const string WorkerCallbackEvidenceVariable = "EMBODYSENSE_RECONCILIATION_PROBE_CALLBACK_EVIDENCE";
    private const string WorkerReadyVariable = "EMBODYSENSE_RECONCILIATION_PROBE_READY";
    private const string WorkerGateVariable = "EMBODYSENSE_RECONCILIATION_PROBE_GATE";
    private const string WorkerResultVariable = "EMBODYSENSE_RECONCILIATION_PROBE_RESULT";
    private const string WorkerOperationVariable = "EMBODYSENSE_RECONCILIATION_PROBE_OPERATION";
    private const int VstestCrashExitCode = 1;
    private static readonly TimeSpan _workerTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(GovernedLoopEffectReconciliationPersistenceBoundary.ProbeReservationPublished, 0, GovernedLoopEffectReconciliationOperationStatus.RepairRequired)]
    [InlineData(GovernedLoopEffectReconciliationPersistenceBoundary.ProbeReceiptPublished, 1, GovernedLoopEffectReconciliationOperationStatus.Replayed)]
    public async Task External_process_loss_restarts_without_repeating_the_probe_callback(
        GovernedLoopEffectReconciliationPersistenceBoundary boundary,
        int expectedCallbackCount,
        GovernedLoopEffectReconciliationOperationStatus expectedRecoveryStatus)
    {
        using var workspace = new TestWorkspace();
        await GovernedLoopEffectReconciliationProbeProcessFixture.SeedAsync(workspace.RootPath);
        var operationId = boundary switch
        {
            GovernedLoopEffectReconciliationPersistenceBoundary.ProbeReservationPublished => "external-probe-reservation",
            GovernedLoopEffectReconciliationPersistenceBoundary.ProbeReceiptPublished => "external-probe-receipt",
            _ => throw new ArgumentOutOfRangeException(nameof(boundary), boundary, "The probe crash boundary is not supported by this fixture.")
        };
        var callbackEvidencePath = workspace.File("probe-callbacks.log");
        var crashReadyPath = workspace.File("probe-crash-ready");
        var crashGatePath = workspace.File("probe-crash-gate");
        var crashResultPath = workspace.File("probe-crash-result");

        var crash = StartWorker("crash", workspace.RootPath, "crash", boundary, callbackEvidencePath, crashReadyPath, crashGatePath, crashResultPath, operationId);
        using (crash.EvidenceCancellation)
        using (crash.Process)
        {
            await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("reconciliation-probe/crash", [crash], _workerTimeout);
            await File.WriteAllTextAsync(crashGatePath, "release");
            await AssertExpectedCrashAsync(crash, boundary);
        }

        Assert.Equal(expectedCallbackCount, ReadCallbackCount(callbackEvidencePath));
        var recoveryResultPath = workspace.File("probe-recovery-result");
        var recoveryGatePath = workspace.File("probe-recovery-gate");
        var recovery = StartWorker("recovery", workspace.RootPath, "recover", null, callbackEvidencePath, workspace.File("probe-recovery-ready"), recoveryGatePath, recoveryResultPath, operationId);
        using (recovery.EvidenceCancellation)
        using (recovery.Process)
        {
            await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("reconciliation-probe/recovery", [recovery], _workerTimeout);
            await File.WriteAllTextAsync(recoveryGatePath, "release");
            await CrossProcessReadinessDiagnostics.WaitForChildrenCompletedAsync("reconciliation-probe/recovery", "probe replay", [recovery], _workerTimeout, CrossProcessReadinessDiagnostics.CoverageChildTeardownTimeout);
        }

        Assert.Equal(expectedRecoveryStatus.ToString(), await File.ReadAllTextAsync(recoveryResultPath));
        Assert.Equal(expectedCallbackCount, ReadCallbackCount(callbackEvidencePath));
        await AssertCanonicalStateAsync(workspace.RootPath, boundary == GovernedLoopEffectReconciliationPersistenceBoundary.ProbeReceiptPublished ? 1 : 0);
    }

    [Fact]
    public async Task Concurrent_external_probe_processes_invoke_one_callback_and_publish_one_observation()
    {
        using var workspace = new TestWorkspace();
        await GovernedLoopEffectReconciliationProbeProcessFixture.SeedAsync(workspace.RootPath);
        var callbackEvidencePath = workspace.File("probe-concurrent-callbacks.log");
        var gatePath = workspace.File("probe-concurrent-gate");
        var firstOperationId = "external-probe-concurrent-first";
        var secondOperationId = "external-probe-concurrent-second";
        var first = StartWorker("first", workspace.RootPath, "concurrent", null, callbackEvidencePath, workspace.File("probe-first-ready"), gatePath, workspace.File("probe-first-result"), firstOperationId);
        var second = StartWorker("second", workspace.RootPath, "concurrent", null, callbackEvidencePath, workspace.File("probe-second-ready"), gatePath, workspace.File("probe-second-result"), secondOperationId);
        using (first.EvidenceCancellation)
        using (second.EvidenceCancellation)
        using (first.Process)
        using (second.Process)
        {
            await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("reconciliation-probe/concurrent", [first, second], _workerTimeout);
            await File.WriteAllTextAsync(gatePath, "release");
            await CrossProcessReadinessDiagnostics.WaitForChildrenCompletedAsync("reconciliation-probe/concurrent", "probe results", [first, second], _workerTimeout, CrossProcessReadinessDiagnostics.CoverageChildTeardownTimeout);
        }

        var results = new[]
        {
            (OperationId: firstOperationId, Status: Enum.Parse<GovernedLoopEffectReconciliationOperationStatus>(await File.ReadAllTextAsync(first.ResultPath))),
            (OperationId: secondOperationId, Status: Enum.Parse<GovernedLoopEffectReconciliationOperationStatus>(await File.ReadAllTextAsync(second.ResultPath)))
        };
        var winner = Assert.Single(results, result => result.Status == GovernedLoopEffectReconciliationOperationStatus.Applied);
        Assert.Single(results, result => result.Status is GovernedLoopEffectReconciliationOperationStatus.Conflict or GovernedLoopEffectReconciliationOperationStatus.RepairRequired or GovernedLoopEffectReconciliationOperationStatus.Unavailable);
        Assert.Equal(1, ReadCallbackCount(callbackEvidencePath));
        Assert.All(ReadCallbackEvidence(callbackEvidencePath), value =>
        {
            Assert.StartsWith("probe-", value, StringComparison.Ordinal);
            Assert.NotEqual(firstOperationId, value);
            Assert.NotEqual(secondOperationId, value);
        });
        await AssertCanonicalStateAsync(workspace.RootPath, 1);

        var replayResultPath = workspace.File("probe-replay-result");
        var replayGatePath = workspace.File("probe-replay-gate");
        var replay = StartWorker("replay", workspace.RootPath, "recover", null, callbackEvidencePath, workspace.File("probe-replay-ready"), replayGatePath, replayResultPath, winner.OperationId);
        using (replay.EvidenceCancellation)
        using (replay.Process)
        {
            await CrossProcessReadinessDiagnostics.WaitForChildrenReadyAsync("reconciliation-probe/replay", [replay], _workerTimeout);
            await File.WriteAllTextAsync(replayGatePath, "release");
            await CrossProcessReadinessDiagnostics.WaitForChildrenCompletedAsync("reconciliation-probe/replay", "exact replay", [replay], _workerTimeout, CrossProcessReadinessDiagnostics.CoverageChildTeardownTimeout);
        }
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed.ToString(), await File.ReadAllTextAsync(replayResultPath));
        Assert.Equal(1, ReadCallbackCount(callbackEvidencePath));
    }

    [Fact]
    public async Task Cross_process_probe_worker()
    {
        var workspaceRoot = Environment.GetEnvironmentVariable(WorkerWorkspaceVariable);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            Assert.True(UsesExpectedTerminationVstestHost("crash"));
            Assert.False(UsesExpectedTerminationVstestHost("recover"));
            Assert.False(UsesExpectedTerminationVstestHost("concurrent"));
            Assert.Throws<ArgumentOutOfRangeException>(() => UsesExpectedTerminationVstestHost("invalid"));
            return;
        }

        var mode = Environment.GetEnvironmentVariable(WorkerModeVariable)
            ?? throw new InvalidOperationException("The reconciliation probe worker mode is required.");
        var callbackEvidencePath = Environment.GetEnvironmentVariable(WorkerCallbackEvidenceVariable)
            ?? throw new InvalidOperationException("The reconciliation probe callback evidence path is required.");
        var readyPath = Environment.GetEnvironmentVariable(WorkerReadyVariable)
            ?? throw new InvalidOperationException("The reconciliation probe readiness path is required.");
        var gatePath = Environment.GetEnvironmentVariable(WorkerGateVariable)
            ?? throw new InvalidOperationException("The reconciliation probe gate path is required.");
        var resultPath = Environment.GetEnvironmentVariable(WorkerResultVariable)
            ?? throw new InvalidOperationException("The reconciliation probe result path is required.");
        var operationId = Environment.GetEnvironmentVariable(WorkerOperationVariable)
            ?? throw new InvalidOperationException("The reconciliation probe operation identity is required.");
        var boundaryText = Environment.GetEnvironmentVariable(WorkerBoundaryVariable);
        var boundary = string.IsNullOrEmpty(boundaryText)
            ? (GovernedLoopEffectReconciliationPersistenceBoundary?)null
            : Enum.Parse<GovernedLoopEffectReconciliationPersistenceBoundary>(boundaryText);
        var fixture = GovernedLoopEffectReconciliationProbeProcessFixture.Create(workspaceRoot, callbackEvidencePath);
        var options = boundary is null
            ? null
            : new GovernedLoopEffectReconciliationCaseStoreOptions
            {
                DurableBoundaryObserver = observed =>
                {
                    if (observed == boundary)
                    {
                        File.WriteAllText(resultPath, observed.ToString());
                        Environment.Exit(73);
                    }
                }
            };
        var service = fixture.CreateService(workspaceRoot, options);
        await File.WriteAllTextAsync(readyPath, mode);
        await WaitForFileAsync(gatePath, _workerTimeout);
        var result = await service.ProbeAsync(new GovernedLoopEffectReconciliationProbeRequest(
            operationId,
            new GovernedLoopEffectReconciliationCaseReference(
                fixture.Case.CaseId,
                fixture.Case.CaseVersion,
                fixture.Case.ContentHash,
                fixture.Case.Binding.ContentHash)));
        await File.WriteAllTextAsync(resultPath, result.Status.ToString());
    }

    private static CrossProcessReadinessChild StartWorker(
        string label,
        string workspaceRoot,
        string mode,
        GovernedLoopEffectReconciliationPersistenceBoundary? boundary,
        string callbackEvidencePath,
        string readyPath,
        string gatePath,
        string resultPath,
        string operationId)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var assemblyPath = typeof(GovernedLoopEffectReconciliationProbeProcessTests).Assembly.Location;
        var testName = $"{typeof(GovernedLoopEffectReconciliationProbeProcessTests).FullName}.{nameof(Cross_process_probe_worker)}";
        if (UsesExpectedTerminationVstestHost(mode))
        {
            CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(startInfo, assemblyPath, testName);
        }
        else
        {
            // These children prove real process coordination while the outer Persistence lane and
            // in-process service/store suites cover the same successful production paths.
            CoverageChildProcessAssembly.AddCoordinationOnlyVstestArguments(startInfo, assemblyPath, testName);
        }
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[WorkerWorkspaceVariable] = workspaceRoot;
        startInfo.Environment[WorkerModeVariable] = mode;
        startInfo.Environment[WorkerBoundaryVariable] = boundary?.ToString() ?? string.Empty;
        startInfo.Environment[WorkerCallbackEvidenceVariable] = callbackEvidencePath;
        startInfo.Environment[WorkerReadyVariable] = readyPath;
        startInfo.Environment[WorkerGateVariable] = gatePath;
        startInfo.Environment[WorkerResultVariable] = resultPath;
        startInfo.Environment[WorkerOperationVariable] = operationId;
        var process = CrossProcessProcessOwnership.Start(startInfo);
        var evidenceCancellation = new CancellationTokenSource();
        return new CrossProcessReadinessChild(
            label,
            process,
            readyPath,
            resultPath,
            process.ReadStandardOutputToEndAsync(evidenceCancellation.Token),
            process.ReadStandardErrorToEndAsync(evidenceCancellation.Token),
            evidenceCancellation);
    }

    private static bool UsesExpectedTerminationVstestHost(string mode)
        => mode switch
        {
            "crash" => true,
            "recover" or "concurrent" => false,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The reconciliation probe worker mode is not supported.")
        };

    private static async Task AssertExpectedCrashAsync(
        CrossProcessReadinessChild worker,
        GovernedLoopEffectReconciliationPersistenceBoundary boundary)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(worker.ResultPath))
        {
            if (worker.Process.HasExited)
            {
                var output = await worker.StandardOutputTask!.WaitAsync(TimeSpan.FromSeconds(5));
                var childError = await worker.StandardErrorTask!.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail($"The reconciliation probe crash worker exited with `{worker.Process.ExitCode}` before publishing `{boundary}`.{Environment.NewLine}{childError}{Environment.NewLine}{output}");
            }
            if (wait.Elapsed >= _workerTimeout)
            {
                worker.Ownership.TerminateProcessTree();
                await worker.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var output = await worker.StandardOutputTask!.WaitAsync(TimeSpan.FromSeconds(5));
                var childError = await worker.StandardErrorTask!.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail($"The reconciliation probe crash worker did not publish `{boundary}` within {_workerTimeout.TotalSeconds:0} seconds.{Environment.NewLine}{childError}{Environment.NewLine}{output}");
            }
            await Task.Delay(10);
        }
        await worker.Process.WaitForExitAsync().WaitAsync(_workerTimeout);
        Assert.Equal(boundary.ToString(), await File.ReadAllTextAsync(worker.ResultPath));
        Assert.Equal(VstestCrashExitCode, worker.Process.ExitCode);
        var error = await worker.StandardErrorTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("test run was aborted", error, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertCanonicalStateAsync(string workspaceRoot, int expectedObservationCount)
    {
        var fixture = GovernedLoopEffectReconciliationProbeProcessFixture.Create(workspaceRoot, string.Empty);
        var paths = new WorkspacePaths(workspaceRoot);
        var store = new GovernedLoopEffectReconciliationCaseStore(new GovernedLoopEffectAttemptStore(paths));
        var page = await store.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(10));
        var summary = Assert.Single(page.Cases);
        Assert.Equal(GovernedLoopEffectReconciliationCaseListStatus.Ready, page.Status);
        var read = await store.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(
            new GovernedLoopEffectReconciliationCaseReference(summary.CaseId, summary.CaseVersion, summary.ContentHash, summary.BindingHash)));
        Assert.Equal(GovernedLoopEffectReconciliationCaseReadStatus.Found, read.Status);
        Assert.Equal(expectedObservationCount, read.Case!.ObservationHistory.Count);
        Assert.Equal(fixture.Case.CaseVersion + expectedObservationCount, read.Case.CaseVersion);
        var effect = await new GovernedLoopEffectAttemptStore(paths).ResumeAsync(fixture.Attempt.Payload.OperationId, fixture.Attempt.Payload.EffectGeneration);
        effect.Lease?.Dispose();
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, effect.Status);
        Assert.Equal(fixture.Attempt.ContentHash, effect.Attempt?.ContentHash);
    }

    private static int ReadCallbackCount(string path)
        => ReadCallbackEvidence(path).Count;

    private static IReadOnlyList<string> ReadCallbackEvidence(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var prefix = Path.GetFileName(path) + ".";
        return Directory.EnumerateFiles(directory, "*.callback")
            .Where(candidate => Path.GetFileName(candidate).StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(candidate => File.ReadAllText(candidate).Trim())
            .ToArray();
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (wait.Elapsed >= timeout)
            {
                throw new TimeoutException($"The reconciliation probe worker did not publish `{path}` within {timeout.TotalSeconds:0} seconds.");
            }
            await Task.Delay(10);
        }
    }
}
