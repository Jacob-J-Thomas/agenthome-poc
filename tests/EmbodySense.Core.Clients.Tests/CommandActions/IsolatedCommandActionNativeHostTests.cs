using System.Runtime.CompilerServices;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Clients.Capabilities;
using EmbodySense.Core.Clients.CommandActions;
using EmbodySense.Core.Clients.Tests.Capabilities;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Secrets.Redaction.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Clients.Tests.CommandActions;

public sealed class IsolatedCommandActionNativeHostTests
{
    [WindowsFact]
    public async Task Default_cross_platform_and_credential_postures_fail_closed_with_zero_process_starts()
    {
        var evidence = new InMemoryCommandActionEvidenceStore();
        var boundary = new TestCommandActionProcessIsolationBoundary();
        var host = new IsolatedCommandActionNativeHost(evidence, isolationBoundary: boundary);
        var registration = CommandActionClientTestData.Registration();
        var credential = CommandActionClientTestData.Registration(credentials: true);

        Assert.Equal(EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Unavailable, host.CheckAvailability(registration).Status);
        Assert.Equal(EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Unavailable, host.CheckAvailability(credential).Status);
        Assert.Null(await host.PrepareAsync(registration, CommandActionClientTestData.Input(registration, "literal")));
        Assert.Equal(0, boundary.Starts);
        Assert.Throws<PlatformNotSupportedException>(() => DenyingCommandActionProcessIsolationBoundary.Instance.StartIsolated(new(), registration, null!));
        Assert.False(await DenyingCommandActionProcessIsolationBoundary.Instance.ProveProcessTreeTerminalAsync(null!));
    }

    [Fact]
    public void Explicit_platform_isolation_adapter_controls_availability_without_an_operating_system_shortcut()
    {
        var evidence = new InMemoryCommandActionEvidenceStore();
        var boundary = new TestCommandActionProcessIsolationBoundary();
        var host = new IsolatedCommandActionNativeHost(
            evidence,
            isolationBoundary: boundary,
            concurrencyGate: new TestCommandActionConcurrencyGate());
        var registration = CommandActionClientTestData.Registration();

        var availability = host.CheckAvailability(registration);

        Assert.Equal(EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Available, availability.Status);
        Assert.Equal(boundary.Availability.Detail, availability.Detail);

        var workspaceTarget = WorkspaceTargetRegistration();
        var targetAvailability = host.CheckAvailability(workspaceTarget);
        Assert.Equal(EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Unavailable, targetAvailability.Status);
        Assert.Contains("workspace-target", targetAvailability.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executable_availability_requires_the_exact_current_artifact_lease_and_launch_fence()
    {
        using var artifact = PrepareArtifact();
        var registration = CommandActionClientTestData.Registration(artifact.EntryPoint);
        var evidence = new InMemoryCommandActionEvidenceStore();
        var available = Host(evidence, artifact, new TestCommandActionProcessIsolationBoundary());
        var missing = new IsolatedCommandActionNativeHost(
            evidence,
            DenyingCapabilityExecutableArtifactResolver.Instance,
            new TestCommandActionProcessIsolationBoundary(),
            new TestCommandActionConcurrencyGate());

        var ready = await available.CheckExecutableAvailabilityAsync(registration);
        var unavailable = await missing.CheckExecutableAvailabilityAsync(registration);

        Assert.Equal(EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Available, ready.Status);
        Assert.Equal(EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Unavailable, unavailable.Status);
        Assert.Contains("artifact", unavailable.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nested_catalog_availability_uses_reentrant_launch_fence_and_revalidates_currentness()
    {
        using var workspace = new TestWorkspace();
        var executablePath = workspace.File("command.exe");
        await File.WriteAllTextAsync(executablePath, "test executable");
        var registration = CommandActionClientTestData.Registration();
        var resolver = new ReentrantCurrentArtifactResolver(workspace.RootPath, registration.Template.ArtifactDigest, registration.Template.ActivationRevision);
        var host = new IsolatedCommandActionNativeHost(
            new InMemoryCommandActionEvidenceStore(),
            resolver,
            new TestCommandActionProcessIsolationBoundary(),
            new TestCommandActionConcurrencyGate());
        var available = await host.CheckExecutableAvailabilityAsync(registration);
        resolver.Current = false;
        var stale = await host.CheckExecutableAvailabilityAsync(registration);

        Assert.Equal(EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Available, available.Status);
        Assert.Equal(EmbodySense.Core.Application.Capabilities.Models.CapabilityExecutableAvailabilityStatus.Unavailable, stale.Status);
        Assert.Equal(2, resolver.ExecuteLaunchFenceCalls);
        Assert.Equal(0, resolver.AcquireLaunchFenceCalls);
    }

    [WindowsFact]
    public async Task Literal_tokens_cross_the_process_boundary_unchanged_but_arbitrary_input_canaries_are_redacted_from_durable_evidence()
    {
        using var artifact = PrepareArtifact();
        var registration = CommandActionClientTestData.Registration(artifact.EntryPoint);
        var evidence = new InMemoryCommandActionEvidenceStore();
        var boundary = new TestCommandActionProcessIsolationBoundary();
        var dispatch = new RecordingActuatorDispatchBoundary();
        boundary.BoundaryWasCrossed = () => dispatch.Crossed;
        var host = Host(evidence, artifact, boundary);
        const string InputCanary = "opaque-canary-7f52c313";
        var input = CommandActionClientTestData.Input(registration, "literal", input: InputCanary);
        var prepared = await host.PrepareAsync(registration, input);

        var result = await host.ExecuteAsync(Request(registration, input, prepared!), dispatch);

        Assert.Equal(CommandActionNativeExecutionStatus.OutcomeObserved, result.Status);
        Assert.Equal(1, dispatch.Calls);
        Assert.Equal(1, boundary.Starts);
        Assert.Equal(string.Empty, boundary.LastStartInfo!.Arguments);
        Assert.Equal(["command-action", "literal", "space ; && $(literal) Ω"], boundary.LastStartInfo.ArgumentList);
        Assert.Equal(new Dictionary<string, string?> { ["A"] = "literal", ["Z"] = "governed" }, boundary.LastStartInfo.Environment);
        var outcome = Assert.Single(evidence.Outcomes);
        Assert.Equal(CommandActionOutcomeKind.Succeeded, outcome.Outcome);
        Assert.True(EmbodySense.Core.Common.Loops.Execution.Effects.GovernedActuatorInputContract.TryCanonicalize(outcome.RetainedStandardOutput, out _, out _));
        Assert.DoesNotContain(InputCanary, outcome.RetainedStandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("space ; && $(literal) Ω", outcome.RetainedStandardOutput, StringComparison.Ordinal);
        Assert.True(outcome.RedactionApplied);
        Assert.Equal(RedactionStatus.Completed, outcome.RedactionSummary.Status);
        Assert.True(outcome.RedactionSummary.ReplacementCount >= 2);
        Assert.DoesNotContain(Environment.GetEnvironmentVariable("PATH") ?? "ambient-path-canary", outcome.RetainedStandardOutput, StringComparison.Ordinal);
    }

    [WindowsTheory]
    [InlineData("nonzero", CommandActionOutcomeKind.NonZeroExit)]
    [InlineData("malformed", CommandActionOutcomeKind.MalformedResult)]
    [InlineData("invalid-encoding", CommandActionOutcomeKind.InvalidEncoding)]
    [InlineData("overflow", CommandActionOutcomeKind.OutputLimitExceeded)]
    public async Task Process_failures_are_distinct_bounded_redacted_conclusive_evidence(string behavior, CommandActionOutcomeKind expected)
    {
        using var artifact = PrepareArtifact();
        var outputBytes = behavior == "overflow" ? 1_024 : 16_384;
        var registration = CommandActionClientTestData.Registration(artifact.EntryPoint, outputBytes: outputBytes);
        var evidence = new InMemoryCommandActionEvidenceStore();
        var boundary = new TestCommandActionProcessIsolationBoundary();
        var dispatch = new RecordingActuatorDispatchBoundary();
        boundary.BoundaryWasCrossed = () => dispatch.Crossed;
        var host = Host(evidence, artifact, boundary);
        var input = CommandActionClientTestData.Input(registration, behavior);
        var prepared = await host.PrepareAsync(registration, input);

        var result = await host.ExecuteAsync(Request(registration, input, prepared!), dispatch);

        Assert.Equal(CommandActionNativeExecutionStatus.OutcomeObserved, result.Status);
        var outcome = Assert.Single(evidence.Outcomes);
        Assert.Equal(expected, outcome.Outcome);
        Assert.True(outcome.ObservedStandardOutputBytes + outcome.ObservedStandardErrorBytes <= outputBytes + 1);
        Assert.DoesNotContain("secret-canary", outcome.RetainedStandardError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("private", outcome.RetainedStandardError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task Supplementary_unicode_at_the_retention_boundary_remains_a_conclusive_outcome()
    {
        using var artifact = PrepareArtifact();
        var registration = CommandActionClientTestData.Registration(artifact.EntryPoint);
        var evidence = new InMemoryCommandActionEvidenceStore();
        var boundary = new TestCommandActionProcessIsolationBoundary();
        var dispatch = new RecordingActuatorDispatchBoundary();
        boundary.BoundaryWasCrossed = () => dispatch.Crossed;
        var host = Host(evidence, artifact, boundary);
        var input = CommandActionClientTestData.Input(registration, "unicode-boundary");
        var prepared = await host.PrepareAsync(registration, input);

        var result = await host.ExecuteAsync(Request(registration, input, prepared!), dispatch);

        Assert.Equal(CommandActionNativeExecutionStatus.OutcomeObserved, result.Status);
        var outcome = Assert.Single(evidence.Outcomes);
        Assert.Equal(CommandActionOutcomeKind.MalformedResult, outcome.Outcome);
        Assert.Equal(CommandActionContractLimits.MaxRetainedOutputCharacters - 1, outcome.RetainedStandardOutput!.Length);
        Assert.DoesNotContain(outcome.RetainedStandardOutput, character => char.IsSurrogate(character));
    }

    [WindowsTheory]
    [InlineData(false, CommandActionOutcomeKind.TimedOut)]
    [InlineData(true, CommandActionOutcomeKind.Cancelled)]
    public async Task Timeout_and_cancellation_require_affirmative_full_tree_termination(bool cancel, CommandActionOutcomeKind expected)
    {
        using var artifact = PrepareArtifact();
        var registration = CommandActionClientTestData.Registration(artifact.EntryPoint, executionMilliseconds: cancel ? 5_000 : 100);
        var evidence = new InMemoryCommandActionEvidenceStore();
        var boundary = new TestCommandActionProcessIsolationBoundary();
        var dispatch = new RecordingActuatorDispatchBoundary();
        boundary.BoundaryWasCrossed = () => dispatch.Crossed;
        var host = Host(evidence, artifact, boundary);
        var input = CommandActionClientTestData.Input(registration, "hang");
        var prepared = await host.PrepareAsync(registration, input);
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            cancellation.CancelAfter(150);
        }

        var result = await host.ExecuteAsync(Request(registration, input, prepared!), dispatch, cancellation.Token);

        Assert.Equal(CommandActionNativeExecutionStatus.OutcomeObserved, result.Status);
        var outcome = Assert.Single(evidence.Outcomes);
        Assert.Equal(expected, outcome.Outcome);
        Assert.Equal(CommandActionTerminationPosture.ProcessTreeTerminated, outcome.Termination);
    }

    [WindowsFact]
    public async Task Isolation_rejection_is_conclusive_but_unknown_tree_termination_remains_ambiguous()
    {
        using var artifact = PrepareArtifact();
        var rejectedRegistration = CommandActionClientTestData.Registration(artifact.EntryPoint);
        var rejectedEvidence = new InMemoryCommandActionEvidenceStore();
        var rejectedBoundary = new TestCommandActionProcessIsolationBoundary { RejectBeforeStart = true };
        var rejectedDispatch = new RecordingActuatorDispatchBoundary();
        rejectedBoundary.BoundaryWasCrossed = () => rejectedDispatch.Crossed;
        var rejectedHost = Host(rejectedEvidence, artifact, rejectedBoundary);
        var rejectedInput = CommandActionClientTestData.Input(rejectedRegistration, "literal");
        var rejectedBefore = await rejectedHost.PrepareAsync(rejectedRegistration, rejectedInput);

        var rejected = await rejectedHost.ExecuteAsync(Request(rejectedRegistration, rejectedInput, rejectedBefore!), rejectedDispatch);

        Assert.Equal(CommandActionNativeExecutionStatus.OutcomeObserved, rejected.Status);
        Assert.Equal(CommandActionOutcomeKind.IsolationRejected, Assert.Single(rejectedEvidence.Outcomes).Outcome);
        Assert.Equal(0, rejectedBoundary.Starts);

        var ambiguousRegistration = CommandActionClientTestData.Registration(artifact.EntryPoint, executionMilliseconds: 100);
        var ambiguousEvidence = new InMemoryCommandActionEvidenceStore();
        var ambiguousBoundary = new TestCommandActionProcessIsolationBoundary { TerminalProof = false };
        var ambiguousDispatch = new RecordingActuatorDispatchBoundary();
        ambiguousBoundary.BoundaryWasCrossed = () => ambiguousDispatch.Crossed;
        var ambiguousHost = Host(ambiguousEvidence, artifact, ambiguousBoundary);
        var ambiguousInput = CommandActionClientTestData.Input(ambiguousRegistration, "hang");
        var ambiguousBefore = await ambiguousHost.PrepareAsync(ambiguousRegistration, ambiguousInput);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ambiguousHost.ExecuteAsync(Request(ambiguousRegistration, ambiguousInput, ambiguousBefore!), ambiguousDispatch));
        Assert.Empty(ambiguousEvidence.Outcomes);
        Assert.True(ambiguousDispatch.Crossed);
    }

    [WindowsFact]
    public async Task Hostile_terminal_proof_is_bounded_by_the_registered_termination_deadline()
    {
        using var artifact = PrepareArtifact();
        var registration = CommandActionClientTestData.Registration(artifact.EntryPoint, terminationMilliseconds: 100);
        var evidence = new InMemoryCommandActionEvidenceStore();
        var boundary = new TestCommandActionProcessIsolationBoundary { NeverCompleteProof = true };
        var dispatch = new RecordingActuatorDispatchBoundary();
        boundary.BoundaryWasCrossed = () => dispatch.Crossed;
        var host = Host(evidence, artifact, boundary);
        var input = CommandActionClientTestData.Input(registration, "literal");
        var prepared = await host.PrepareAsync(registration, input);

        await Assert.ThrowsAsync<TimeoutException>(() => host.ExecuteAsync(Request(registration, input, prepared!), dispatch).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Empty(evidence.Outcomes);
        Assert.True(dispatch.Crossed);
    }

    [WindowsFact]
    public async Task Stale_or_substituted_preparation_and_launch_fence_start_nothing()
    {
        using var artifact = PrepareArtifact();
        var registration = CommandActionClientTestData.Registration(artifact.EntryPoint);
        var evidence = new InMemoryCommandActionEvidenceStore();
        var boundary = new TestCommandActionProcessIsolationBoundary();
        var resolver = new TestCapabilityExecutableArtifactResolver(artifact.RootPath);
        var host = new IsolatedCommandActionNativeHost(evidence, resolver, boundary, new TestCommandActionConcurrencyGate());
        var input = CommandActionClientTestData.Input(registration, "literal");
        var prepared = await host.PrepareAsync(registration, input);
        var request = Request(registration, input, prepared!) with { TargetFingerprint = new string('9', 64) };

        var result = await host.ExecuteAsync(request, new RecordingActuatorDispatchBoundary());

        Assert.Equal(CommandActionNativeExecutionStatus.DispatchNotStarted, result.Status);
        Assert.Equal(0, boundary.Starts);
    }

    private static CommandActionNativeExecutionRequest Request(
        CommandActionRegistration registration,
        EmbodySense.Core.Common.Loops.Execution.Effects.Models.GovernedActuatorInputEvidence input,
        EmbodySense.Core.Application.CommandActions.Models.CommandActionNativePreparation preparation)
        => new(
            registration, input, "effect-alpha", "operation-alpha", 1,
            preparation.Evidence.TargetFingerprint,
            preparation.Evidence.PreconditionEvidenceHash,
            preparation.Evidence.EvidenceId);

    private static IsolatedCommandActionNativeHost Host(
        InMemoryCommandActionEvidenceStore evidence,
        PreparedArtifact artifact,
        TestCommandActionProcessIsolationBoundary boundary)
        => new(
            evidence,
            new TestCapabilityExecutableArtifactResolver(artifact.RootPath),
            boundary,
            new TestCommandActionConcurrencyGate());

    private static PreparedArtifact PrepareArtifact()
    {
        var repositoryRoot = FindRepositoryRoot();
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var redirected = Path.Combine(outputDirectory.Parent!.Parent!.FullName, "EmbodySense.CancellationHost", outputDirectory.Name);
        var source = File.Exists(Path.Combine(redirected, "EmbodySense.CancellationHost.dll"))
            ? redirected
            : Path.Combine(repositoryRoot, "tests", "EmbodySense.CancellationHost", "bin", outputDirectory.Parent.Name, outputDirectory.Name);
        var workspace = new TestWorkspace();
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(workspace.RootPath, Path.GetFileName(file)));
        }
        return new PreparedArtifact(workspace, OperatingSystem.IsWindows() ? "EmbodySense.CancellationHost.exe" : "EmbodySense.CancellationHost");
    }

    private static CommandActionRegistration WorkspaceTargetRegistration()
    {
        var registration = CommandActionClientTestData.Registration();
        var template = CommandActionTemplateContract.Create(
            registration.Template.SchemaVersion,
            registration.Template.Capability,
            registration.Template.Implementation,
            registration.Template.ArtifactDigest,
            registration.Template.ActivationRevision,
            "command/workspace-target",
            1,
            [new CommandActionSlotDefinition("target", CommandActionSlotKind.WorkspaceRelativeTarget, 512, null, null, [], false)],
            [new CommandActionArgumentPart(CommandActionArgumentPartKind.Slot, "target")],
            [],
            CommandActionSecondaryGrammarPolicy.None,
            CommandActionStandardInputKind.Closed,
            null,
            CommandActionOutputKind.Json,
            registration.Template.Isolation,
            false);
        return registration with { Template = template };
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
