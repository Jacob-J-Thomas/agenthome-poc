using System.Diagnostics;
using System.Security.Cryptography;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Tests.Loops.Admission;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Admission.Models;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Admission;

internal static class GovernedLoopAdmissionStoreTestFixture
{
    private const string CrossProcessMode = "EMBODYSENSE_ADMISSION_STORE_MODE";
    private const string CrossProcessWorkspace = "EMBODYSENSE_ADMISSION_STORE_WORKSPACE";
    private const string CrossProcessTrustRoot = "EMBODYSENSE_ADMISSION_STORE_TRUST_ROOT";
    private const string CrossProcessGate = "EMBODYSENSE_ADMISSION_STORE_GATE";
    private const string CrossProcessReady = "EMBODYSENSE_ADMISSION_STORE_READY";
    private const string CrossProcessOutput = "EMBODYSENSE_ADMISSION_STORE_OUTPUT";
    private const string CrossProcessOperation = "EMBODYSENSE_ADMISSION_STORE_OPERATION";
    private const string CrossProcessHostTestName = "EmbodySense.Core.Persistence.Tests.Loops.Admission.GovernedLoopAdmissionStoreCrossProcessHostTests.Cross_process_admission_store_host";
    internal const int ChildReadinessTimeoutSeconds = 60;
    internal const int GateReleaseMarginSeconds = 15;
    internal const int GateTimeoutSeconds = ChildReadinessTimeoutSeconds + GateReleaseMarginSeconds;
    private const int MaximumChildEvidenceCharacters = 8_192;
    internal const char RequestA = '1';
    internal const char RequestB = '4';

    internal static GovernedLoopAdmissionStore Store(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trust,
        GovernedLoopAdmissionStoreOptions? options = null)
        => new(paths, trust, options);

    internal static GovernedLoopAdmissionStoreMutation Mutation(
        WorkspacePaths paths,
        string operationId,
        char requestHash,
        long generation,
        bool admitted = true)
    {
        var workspaceId = WorkspaceId(paths);
        var intent = GovernedLoopAdmissionTestFixture.Intent(
            workspaceId: workspaceId,
            operationId: operationId,
            requestHash: GovernedLoopAdmissionTestFixture.Hash(requestHash));
        GovernedLoopAdmissionTerminalOutcome outcome;
        if (admitted)
        {
            var capabilityAdmission = GovernedLoopAdmissionTestFixture.CapabilityAdmission() with { WorkspaceScopeId = workspaceId };
            var evidence = GovernedLoopAdmissionTestFixture.Evidence(intent, capabilityAdmission: capabilityAdmission);
            var receipt = GovernedLoopAdmissionTestFixture.Receipt(intent, evidence);
            outcome = GovernedLoopAdmissionTestFixture.AdmittedOutcome(intent, receipt);
        }
        else
        {
            var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent);
            outcome = GovernedLoopAdmissionTestFixture.RejectedOutcome(intent, rejection);
        }

        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        return new GovernedLoopAdmissionStoreMutation(
            workspaceId,
            operationId,
            intent.RequestHash,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            generation,
            outcome);
    }

    internal static GovernedLoopAdmissionStoreMutation RejectionMutation(
        WorkspacePaths paths,
        string operationId,
        char requestHash,
        long generation,
        GovernedLoopAdmissionFailureCode failureCode,
        GovernedLoopAdmissionAuthorityDenialProof? authorityDenial = null,
        GovernedLoopAdmissionCapabilityDenialProof? capabilityDenial = null)
    {
        var workspaceId = WorkspaceId(paths);
        var intent = GovernedLoopAdmissionTestFixture.Intent(
            workspaceId: workspaceId,
            operationId: operationId,
            requestHash: GovernedLoopAdmissionTestFixture.Hash(requestHash));
        var rejection = GovernedLoopAdmissionTestFixture.Rejection(intent, failureCode, authorityDenial, capabilityDenial);
        var outcome = GovernedLoopAdmissionTestFixture.RejectedOutcome(intent, rejection);
        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        return new GovernedLoopAdmissionStoreMutation(
            workspaceId,
            operationId,
            intent.RequestHash,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            generation,
            outcome);
    }

    internal static GovernedLoopAdmissionAuthorityDenialProof AuthorityDenialWithProfile()
    {
        Assert.True(AuthorityProfileId.TryParse("bounded-profile", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("3", out var revision, out _));
        Assert.True(AuthorityBoundaryReceiptFactory.TryCreate(
            AuthorityBoundaryReceipt.CurrentSchemaVersion,
            AuthorityBoundaryDecision.Deny,
            [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Deny, AuthorityBoundaryReason.ProfileRetired)],
            [new AuthorityProfileReference(profileId!, revision!)],
            GovernedLoopAdmissionTestFixture.RecordedAtUtc,
            out var receipt,
            out var validation), string.Join(',', validation.Errors));
        var candidate = GovernedLoopAdmissionTestFixture.EffectiveAuthority();
        return new GovernedLoopAdmissionAuthorityDenialProof(
            GovernedLoopAdmissionLimits.CurrentSchemaVersion,
            candidate,
            AuthorityCeilingIntersection.EmptyCeiling(),
            receipt!);
    }

    internal static string WorkspaceId(WorkspacePaths paths) => CapabilityWorkspaceScopeId.Create(paths.RootPath);

    internal static GovernedLoopAdmissionStoreOptions FailAt(GovernedLoopAdmissionPersistenceBoundary boundary)
        => new()
        {
            DurableBoundaryObserver = (observed, _) => observed == boundary
                ? ValueTask.FromException(new IOException("Injected admission durable-boundary interruption."))
                : ValueTask.CompletedTask
        };

    internal static string PrimaryPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "admissions", "terminal-outcomes.json");

    internal static string ProofPath(WorkspacePaths paths)
        => Path.Combine(paths.AgentPath, "loops", "admissions", "terminal-outcomes.proved.json");

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    internal static Process StartCrossProcessHost(
        string mode,
        string workspace,
        string trustRoot,
        string gate,
        string ready,
        string output,
        string operation)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (UsesExpectedTerminationVstestHost(mode))
        {
            Verification.CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(startInfo, typeof(GovernedLoopAdmissionStoreCrossProcessHostTests).Assembly.Location, CrossProcessHostTestName);
        }
        else
        {
            Verification.CoverageChildProcessAssembly.AddVstestArguments(startInfo, typeof(GovernedLoopAdmissionStoreCrossProcessHostTests).Assembly.Location, CrossProcessHostTestName);
        }
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessMode] = mode;
        startInfo.Environment[CrossProcessWorkspace] = workspace;
        startInfo.Environment[CrossProcessTrustRoot] = trustRoot;
        startInfo.Environment[CrossProcessGate] = gate;
        startInfo.Environment[CrossProcessReady] = ready;
        startInfo.Environment[CrossProcessOutput] = output;
        startInfo.Environment[CrossProcessOperation] = operation;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Cross-process admission-store host did not start.");
    }

    internal static async Task RunCrossProcessHostAsync()
    {
        var mode = Environment.GetEnvironmentVariable(CrossProcessMode);
        if (string.IsNullOrEmpty(mode))
        {
            Assert.False(UsesExpectedTerminationVstestHost("writer"));
            Assert.True(UsesExpectedTerminationVstestHost("crash-proof"));
            Assert.True(UsesExpectedTerminationVstestHost("crash-primary"));
            Assert.True(UsesExpectedTerminationVstestHost("crash-trust"));
            Assert.Throws<ArgumentOutOfRangeException>(() => UsesExpectedTerminationVstestHost("success"));
            AssertExpectedTerminationVstestContract();
            return;
        }

        var usesExpectedTerminationHost = UsesExpectedTerminationVstestHost(mode);
        var workspace = Environment.GetEnvironmentVariable(CrossProcessWorkspace)!;
        var trustRoot = Environment.GetEnvironmentVariable(CrossProcessTrustRoot)!;
        var gate = Environment.GetEnvironmentVariable(CrossProcessGate)!;
        var ready = Environment.GetEnvironmentVariable(CrossProcessReady)!;
        var output = Environment.GetEnvironmentVariable(CrossProcessOutput)!;
        var operation = Environment.GetEnvironmentVariable(CrossProcessOperation)!;
        await File.WriteAllTextAsync(ready, "ready");
        await WaitForGateAsync(gate);
        GovernedLoopAdmissionStoreOptions? options = usesExpectedTerminationHost
            ? new GovernedLoopAdmissionStoreOptions
            {
                DurableBoundaryObserver = (boundary, _) =>
                {
                    var target = mode switch
                    {
                        "crash-proof" => GovernedLoopAdmissionPersistenceBoundary.ProofPublished,
                        "crash-primary" => GovernedLoopAdmissionPersistenceBoundary.PrimaryPublished,
                        "crash-trust" => GovernedLoopAdmissionPersistenceBoundary.TrustAdvanced,
                        _ => throw new ArgumentOutOfRangeException(nameof(mode))
                    };
                    if (boundary == target)
                    {
                        Process.GetCurrentProcess().Kill();
                        Thread.Sleep(Timeout.Infinite);
                    }

                    return ValueTask.CompletedTask;
                }
            }
            : null;
        var paths = new WorkspacePaths(workspace);
        var store = new GovernedLoopAdmissionStore(paths, new FileCapabilityCatalogTrustProvider(trustRoot), options);
        var mutation = Mutation(paths, operation, operation.EndsWith("two", StringComparison.Ordinal) ? RequestB : RequestA, 0);
        var retryWindow = Stopwatch.StartNew();
        GovernedLoopAdmissionStoreCommitResult result;
        do
        {
            result = await store.CommitAsync(mutation);
            if (mode != "writer"
                || result.Status != GovernedLoopAdmissionStoreCommitStatus.Unavailable
                || retryWindow.Elapsed >= TimeSpan.FromSeconds(15))
            {
                break;
            }

            await Task.Delay(50);
        }
        while (true);
        await File.WriteAllTextAsync(output, result.Status.ToString());
    }

    internal static bool UsesExpectedTerminationVstestHost(string mode)
        => mode switch
        {
            "writer" => false,
            "crash-proof" or "crash-primary" or "crash-trust" => true,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The admission-store child-process mode is not admitted.")
        };

    private static void AssertExpectedTerminationVstestContract()
    {
        using var workspace = new TestWorkspace();
        var pristineDirectory = workspace.File("pristine");
        var collectorDirectory = workspace.File("Collector");
        Directory.CreateDirectory(pristineDirectory);
        Directory.CreateDirectory(collectorDirectory);
        File.WriteAllText(workspace.File("verification-pull-request.runsettings"), "<RunSettings />");
        var currentAssemblyPath = typeof(GovernedLoopAdmissionStoreCrossProcessHostTests).Assembly.Location;
        var pristineAssemblyPath = Path.Combine(pristineDirectory, Path.GetFileName(currentAssemblyPath));
        File.WriteAllBytes(pristineAssemblyPath, [0x01, 0x02, 0x03, 0x04]);
        File.WriteAllBytes(Path.Combine(pristineDirectory, "dependency.dll"), [0x05, 0x06, 0x07, 0x08]);
        var expectedHashes = GetDirectoryHashes(pristineDirectory);
        var originalDirectory = Environment.GetEnvironmentVariable(Verification.CoverageChildProcessAssembly.IsolatedAssemblyDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(Verification.CoverageChildProcessAssembly.IsolatedAssemblyDirectoryVariable, pristineDirectory);
            var expectedTermination = new ProcessStartInfo("dotnet");
            Verification.CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(expectedTermination, currentAssemblyPath, CrossProcessHostTestName);

            Assert.Equal(
                ["vstest", pristineAssemblyPath, $"--TestCaseFilter:FullyQualifiedName={CrossProcessHostTestName}"],
                expectedTermination.ArgumentList);
            Assert.False(Directory.Exists(workspace.File("Invocations")));
            Assert.False(Directory.Exists(workspace.File("Results")));
            Assert.Equal(expectedHashes, GetDirectoryHashes(pristineDirectory));

            var successful = new ProcessStartInfo("dotnet");
            Verification.CoverageChildProcessAssembly.AddVstestArguments(successful, currentAssemblyPath, CrossProcessHostTestName);
            var resultsDirectory = successful.ArgumentList.Single(argument => argument.StartsWith("--ResultsDirectory:", StringComparison.Ordinal))["--ResultsDirectory:".Length..];
            Assert.Contains("--Collect:XPlat Code Coverage", successful.ArgumentList);
            Assert.StartsWith(workspace.File("Results") + Path.DirectorySeparatorChar, resultsDirectory, StringComparison.Ordinal);
            Assert.Single(Directory.EnumerateDirectories(workspace.File("Invocations")));
            Assert.Single(Directory.EnumerateDirectories(workspace.File("Results")));
            Assert.True(Directory.Exists(workspace.File("Results")));
            Assert.Equal(expectedHashes, GetDirectoryHashes(pristineDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Verification.CoverageChildProcessAssembly.IsolatedAssemblyDirectoryVariable, originalDirectory);
        }
    }

    private static IReadOnlyList<string> GetDirectoryHashes(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => $"{Path.GetRelativePath(directory, path)}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
            .ToArray();

    internal static async Task WaitForChildrenReadyAsync(Process first, string firstReadyPath, Process second, string secondReadyPath)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(firstReadyPath) || !File.Exists(secondReadyPath))
        {
            if (first.HasExited || second.HasExited)
            {
                var exitedLabel = first.HasExited ? "first" : "second";
                var exitedPath = first.HasExited ? firstReadyPath : secondReadyPath;
                var evidence = await StopAndReadChildEvidenceAsync(first, second);
                Assert.Fail($"Cross-process admission writer '{exitedLabel}' exited before publishing `{exitedPath}`.{Environment.NewLine}{evidence}");
            }

            if (wait.Elapsed >= TimeSpan.FromSeconds(ChildReadinessTimeoutSeconds))
            {
                var evidence = await StopAndReadChildEvidenceAsync(first, second);
                Assert.Fail($"Cross-process admission writers did not both report ready within {ChildReadinessTimeoutSeconds} seconds. first_ready={File.Exists(firstReadyPath)} second_ready={File.Exists(secondReadyPath)}{Environment.NewLine}{evidence}");
            }

            await Task.Delay(10);
        }
    }

    internal static async Task WaitForChildReadyAsync(Process process, string readyPath)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(readyPath))
        {
            if (process.HasExited)
            {
                var evidence = await ReadChildEvidenceAsync("child", process);
                Assert.Fail($"Cross-process admission host exited before publishing `{readyPath}`.{Environment.NewLine}{evidence}");
            }

            if (wait.Elapsed >= TimeSpan.FromSeconds(ChildReadinessTimeoutSeconds))
            {
                await StopChildProcessAsync(process);
                var evidence = await ReadChildEvidenceAsync("child", process);
                Assert.Fail($"Cross-process admission host did not report ready within {ChildReadinessTimeoutSeconds} seconds: `{readyPath}`.{Environment.NewLine}{evidence}");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<string> StopAndReadChildEvidenceAsync(Process first, Process second)
    {
        await Task.WhenAll(StopChildProcessAsync(first), StopChildProcessAsync(second));
        var evidence = await Task.WhenAll(ReadChildEvidenceAsync("first", first), ReadChildEvidenceAsync("second", second));
        return string.Join(Environment.NewLine, evidence);
    }

    private static async Task StopChildProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task<string> ReadChildEvidenceAsync(string label, Process process)
    {
        if (!process.HasExited)
        {
            return $"{label}: pid={process.Id} exit=<still-running> stdout=<unavailable> stderr=<unavailable>";
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(outputTask, errorTask);
        return $"{label}: pid={process.Id} exit={process.ExitCode} stdout={BoundChildEvidence(outputTask.Result)} stderr={BoundChildEvidence(errorTask.Result)}";
    }

    private static string BoundChildEvidence(string evidence)
    {
        if (string.IsNullOrEmpty(evidence))
        {
            return "<empty>";
        }

        return evidence.Length <= MaximumChildEvidenceCharacters
            ? evidence
            : "<truncated>" + evidence[^MaximumChildEvidenceCharacters..];
    }

    internal static Task WaitForGateAsync(string path)
        => WaitForGateAsync(path, TimeSpan.FromSeconds(GateTimeoutSeconds));

    internal static async Task WaitForGateAsync(string path, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < timeout, $"Cross-process admission host did not observe gate `{path}`.");
            await Task.Delay(10);
        }
    }

    internal static async Task AssertProcessSucceededAsync(Process process)
    {
        var error = await process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error + Environment.NewLine + output);
    }
}
