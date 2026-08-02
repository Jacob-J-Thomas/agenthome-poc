using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

public sealed class CredentialLifecyclePersistenceRestartTests
{
    private static readonly DateTimeOffset _timestamp = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CredentialRepairCrashWindow.AfterDurableIntent)]
    [InlineData(CredentialRepairCrashWindow.AfterProviderSuccess)]
    public async Task InterruptedRepairReconciliationSurvivesFreshStoresServicesAndProvidersWithoutImplicitRetry(CredentialRepairCrashWindow crashWindow)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedPreparedRegistrationAsync(paths);
        var deleteCount = new StrongBox<int>();
        var initialService = Service(paths, TestTrust(paths), deleteCount);
        var repairPreview = await initialService.PreviewAsync(PreviewRequest("restart-repair", CredentialLifecycleOperationKind.Repair, 2));
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, repairPreview.Status);
        var repair = Request("restart-repair", CredentialLifecycleOperationKind.Repair, 2, repairPreview);
        var providerEntryMarker = Path.Combine(workspace.RootPath, "provider-entered.marker");
        var providerSuccessMarker = Path.Combine(workspace.RootPath, "provider-success.marker");
        using var crashHost = StartCredentialRepairCrashHost(workspace.RootPath, crashWindow, providerEntryMarker, providerSuccessMarker);
        try
        {
            var expectedMarker = crashWindow == CredentialRepairCrashWindow.AfterProviderSuccess ? providerSuccessMarker : providerEntryMarker;
            await WaitForFileAsync(expectedMarker, TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(providerEntryMarker));
            Assert.Equal(crashWindow == CredentialRepairCrashWindow.AfterProviderSuccess, File.Exists(providerSuccessMarker));
            var durableIntent = await Store(paths).ReadAsync();
            Assert.Equal(3, durableIntent.RegistryRevision);
            Assert.Equal(CredentialLifecycleMutationPhase.Intent, Assert.Single(durableIntent.Operations, operation => operation.OperationId.Equals(repair.OperationId)).LifecyclePhase);
        }
        finally
        {
            if (!crashHost.HasExited)
            {
                crashHost.Kill(entireProcessTree: true);
            }
            await crashHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.NotEqual(0, crashHost.ExitCode);
        Assert.Equal(crashWindow == CredentialRepairCrashWindow.AfterProviderSuccess, File.Exists(providerSuccessMarker));

        var callsAfterInterruption = 0;
        var restartedService = Service(paths, TestTrust(paths), deleteCount);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await restartedService.ExecuteAsync(repair)).Status);
        Assert.Equal(callsAfterInterruption, deleteCount.Value);
        var interrupted = await Store(paths).ReadAsync();
        Assert.Equal(3, interrupted.RegistryRevision);
        Assert.Equal(CredentialLifecycleMutationPhase.Intent, Assert.Single(interrupted.Operations, operation => operation.OperationId.Equals(repair.OperationId)).LifecyclePhase);
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, (await restartedService.PreviewAsync(PreviewRequest("restart-repair-blocked", CredentialLifecycleOperationKind.Repair, 3))).Status);

        var reconciliationService = Service(paths, TestTrust(paths), deleteCount);
        var reconcilePreviewRequest = PreviewRequest("restart-reconcile", CredentialLifecycleOperationKind.ReconcileRepair, 3, repair.OperationId);
        var reconcilePreview = await reconciliationService.PreviewAsync(reconcilePreviewRequest);
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, reconcilePreview.Status);
        var reconcile = Request("restart-reconcile", CredentialLifecycleOperationKind.ReconcileRepair, 3, reconcilePreview, repair.OperationId);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await reconciliationService.ExecuteAsync(reconcile)).Status);
        Assert.Equal(callsAfterInterruption, deleteCount.Value);

        var replayService = Service(paths, TestTrust(paths), deleteCount);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, (await replayService.ExecuteAsync(reconcile)).Status);
        Assert.Equal(callsAfterInterruption, deleteCount.Value);
        var reconciled = await Store(paths).ReadAsync();
        Assert.Equal(4, reconciled.RegistryRevision);
        Assert.True(Assert.Single(reconciled.Tombstones).NeedsRepair);

        var finalService = Service(paths, TestTrust(paths), deleteCount);
        var finalPreview = await finalService.PreviewAsync(PreviewRequest("restart-final-repair", CredentialLifecycleOperationKind.Repair, 4));
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, finalPreview.Status);
        var finalRepair = Request("restart-final-repair", CredentialLifecycleOperationKind.Repair, 4, finalPreview);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await finalService.ExecuteAsync(finalRepair)).Status);
        Assert.Equal(callsAfterInterruption + 1, deleteCount.Value);
        var final = await Store(paths).ReadAsync();
        Assert.Equal(6, final.RegistryRevision);
        Assert.False(Assert.Single(final.Tombstones).NeedsRepair);
    }

    [Fact]
    public async Task CompletedRepairResponseLossReplaysWithoutProviderRetryOrReconciliation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedPreparedRegistrationAsync(paths);
        var deleteCount = new StrongBox<int>();
        var service = Service(paths, TestTrust(paths), deleteCount);
        var preview = await service.PreviewAsync(PreviewRequest("completed-repair", CredentialLifecycleOperationKind.Repair, 2));
        var repair = Request("completed-repair", CredentialLifecycleOperationKind.Repair, 2, preview);

        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await service.ExecuteAsync(repair)).Status);
        Assert.Equal(1, deleteCount.Value);
        var restarted = Service(paths, TestTrust(paths), deleteCount);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, (await restarted.ExecuteAsync(repair)).Status);
        Assert.Equal(1, deleteCount.Value);
        var state = await Store(paths).ReadAsync();
        Assert.False(Assert.Single(state.Tombstones).NeedsRepair);
        var reconcile = PreviewRequest("completed-repair-reconcile", CredentialLifecycleOperationKind.ReconcileRepair, state.RegistryRevision!.Value, repair.OperationId);
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, (await restarted.PreviewAsync(reconcile)).Status);
    }

    [Fact]
    public async Task PreparedRegistrationRepairFailureSurvivesRestartUntilExplicitRetry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedPreparedRegistrationAsync(paths);
        var deleteCount = new StrongBox<int>();
        var failingService = Service(paths, TestTrust(paths), deleteCount, deleteSucceeds: false);
        var preview = await failingService.PreviewAsync(PreviewRequest("prepared-repair-failure", CredentialLifecycleOperationKind.Repair, 2));
        var repair = Request("prepared-repair-failure", CredentialLifecycleOperationKind.Repair, 2, preview);

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await failingService.ExecuteAsync(repair)).Status);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await Service(paths, TestTrust(paths), deleteCount).ExecuteAsync(repair)).Status);
        Assert.Equal(1, deleteCount.Value);
        var repairRequired = await Store(paths).ReadAsync();
        Assert.True(Assert.Single(repairRequired.Tombstones).NeedsRepair);

        var finalService = Service(paths, TestTrust(paths), deleteCount);
        var finalPreview = await finalService.PreviewAsync(PreviewRequest("prepared-repair-final", CredentialLifecycleOperationKind.Repair, repairRequired.RegistryRevision!.Value));
        var finalRepair = Request("prepared-repair-final", CredentialLifecycleOperationKind.Repair, repairRequired.RegistryRevision.Value, finalPreview);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await finalService.ExecuteAsync(finalRepair)).Status);
        Assert.Equal(2, deleteCount.Value);
        Assert.False(Assert.Single((await Store(paths).ReadAsync()).Tombstones).NeedsRepair);
    }

    [Fact]
    public async Task CreateInterruptedAfterDurableLocatorPreparationReplaysWithoutEffectsAndRepairsAcrossRestart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var locatorMarker = Path.Combine(workspace.RootPath, "locator-created.marker");
        var providerEntryMarker = Path.Combine(workspace.RootPath, "provider-entered.marker");
        using var crashHost = StartCredentialCreateCrashHost(workspace.RootPath, locatorMarker, providerEntryMarker);
        try
        {
            await WaitForFileAsync(providerEntryMarker, TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(locatorMarker));
            var prepared = await Store(paths).ReadAsync();
            Assert.Equal(2, prepared.RegistryRevision);
            Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorPrepared], prepared.Operations.Select(operation => operation.LifecyclePhase).ToArray());
            Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, Assert.Single(prepared.Entries).Health);
        }
        finally
        {
            if (!crashHost.HasExited)
            {
                crashHost.Kill(entireProcessTree: true);
            }
            await crashHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.NotEqual(0, crashHost.ExitCode);

        var adapter = new CoordinatedCredentialCreateAdapter();
        var provider = new CountingCreateCredentialValueProvider();
        var restarted = CreateService(paths, adapter, provider);
        var create = CreateRequest("restart-create");
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await restarted.ExecuteAsync(create, destination => Fill(destination, 2))).Status);
        Assert.Equal(0, adapter.CreateCount);
        Assert.Equal(0, provider.CreateCount);

        var repairPreview = await restarted.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("restart-create-repair"), CredentialLifecycleOperationKind.Repair, CreateReferenceId(), "workspace-1", Environment.UserName, 2));
        var repair = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, Id("restart-create-repair"), CreateReferenceId(), "workspace-1", Environment.UserName, 2, _timestamp, Preview: repairPreview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await restarted.ExecuteAsync(repair)).Status);
        Assert.Equal(1, provider.DeleteCount);
        var repaired = await Store(paths).ReadAsync();
        Assert.False(Assert.Single(repaired.Tombstones).NeedsRepair);
        Assert.Empty(repaired.PendingAudits);
        var audit = await File.ReadAllTextAsync(paths.EventsLogPath);
        Assert.Contains("restart-create", audit, StringComparison.Ordinal);
        Assert.Contains("restart-create-repair", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentExactCreateAcrossPublicServicesInvokesLocatorAndProviderAtMostOnce()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var adapter = new CoordinatedCredentialCreateAdapter(blockLocator: true);
        var provider = new CountingCreateCredentialValueProvider();
        var firstService = CreateService(paths, adapter, provider);
        var secondService = CreateService(paths, adapter, provider);
        var request = CreateRequest("concurrent-create");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var first = firstService.ExecuteAsync(request, destination => Fill(destination, 1));
        await adapter.WaitForLocatorAsync(timeout.Token);
        var second = secondService.ExecuteAsync(request, destination => Fill(destination, 2));
        adapter.ReleaseLocator();
        var results = await Task.WhenAll(first, second).WaitAsync(timeout.Token);

        Assert.Contains(results, result => result.Status == CredentialLifecycleResultStatus.Applied);
        Assert.Contains(results, result => result.Status == CredentialLifecycleResultStatus.Replayed);
        Assert.Equal(1, adapter.CreateCount);
        Assert.Equal(1, provider.CreateCount);
        var state = await Store(paths).ReadAsync();
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(state.Entries).Health);
        Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorPrepared, CredentialLifecycleMutationPhase.Complete], state.Operations.Select(operation => operation.LifecyclePhase).ToArray());
        Assert.Empty(state.PendingAudits);
    }

    private static CredentialLifecycleService Service(WorkspacePaths paths, FileCapabilityCatalogTrustProvider trustProvider, StrongBox<int> deleteCount, bool deleteSucceeds = true)
    {
        var dependentIndex = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        return CredentialLifecyclePersistenceFactory.Create(paths, trustProvider, CredentialLifecyclePersistenceTestAdapter.Instance, new CountingCredentialValueProvider(deleteCount, deleteSucceeds), CredentialLifecyclePersistenceTestAdapter.Instance, dependentIndex, CredentialLifecyclePersistenceTestAdapter.Instance, new AuditLog(paths));
    }

    private static CredentialLifecycleService CreateService(WorkspacePaths paths, CoordinatedCredentialCreateAdapter adapter, ICredentialValueProvider provider)
    {
        var dependentIndex = new CapabilityDependentIndex([adapter]);
        return CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), adapter, provider, adapter, dependentIndex, adapter, new AuditLog(paths));
    }

    private static Process StartCredentialRepairCrashHost(string workspaceRoot, CredentialRepairCrashWindow crashWindow, string providerEntryMarker, string providerSuccessMarker)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var hostAssembly = Path.Combine(FindRepositoryRoot(), "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework, "EmbodySense.CancellationHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Cancellation host assembly was not built at `{hostAssembly}`.");
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("credential-repair-crash");
        startInfo.ArgumentList.Add(workspaceRoot);
        startInfo.ArgumentList.Add(crashWindow.ToString());
        startInfo.ArgumentList.Add(providerEntryMarker);
        startInfo.ArgumentList.Add(providerSuccessMarker);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The credential repair crash process could not be started.");
    }

    private static Process StartCredentialCreateCrashHost(string workspaceRoot, string locatorMarker, string providerEntryMarker)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var hostAssembly = Path.Combine(FindRepositoryRoot(), "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework, "EmbodySense.CancellationHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Cancellation host assembly was not built at `{hostAssembly}`.");
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("credential-create-crash");
        startInfo.ArgumentList.Add(workspaceRoot);
        startInfo.ArgumentList.Add(locatorMarker);
        startInfo.ArgumentList.Add(providerEntryMarker);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The credential create crash process could not be started.");
    }

    private static Process StartCredentialPayloadCreateCrashHost(string workspaceRoot, string trustProfile, string operationId, string consentId, string referenceJson, string bindingJson, string locatorMarker, string providerEntryMarker)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var hostAssembly = Path.Combine(FindRepositoryRoot(), "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework, "EmbodySense.CancellationHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Cancellation host assembly was not built at `{hostAssembly}`.");
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("credential-create-payload-crash");
        startInfo.ArgumentList.Add(workspaceRoot);
        startInfo.ArgumentList.Add(trustProfile);
        startInfo.ArgumentList.Add(operationId);
        startInfo.ArgumentList.Add(consentId);
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(referenceJson)));
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(bindingJson)));
        startInfo.ArgumentList.Add(locatorMarker);
        startInfo.ArgumentList.Add(providerEntryMarker);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The credential prepared-create crash process could not be started.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(25, cancellation.Token);
        }
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

    private static async Task SeedPreparedRegistrationAsync(WorkspacePaths paths)
    {
        var locatorMarker = Path.Combine(paths.WorkspacePath, "prepared-locator.marker");
        var providerEntryMarker = Path.Combine(paths.WorkspacePath, "prepared-provider-entered.marker");
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        Assert.True(CredentialContractJson.TrySerialize(reference, out var referenceJson, out _));
        Assert.True(CredentialContractJson.TrySerialize(binding, out var bindingJson, out _));
        using var crashHost = StartCredentialPayloadCreateCrashHost(paths.WorkspacePath, "restart", "restart-create", "restart-consent", referenceJson!, bindingJson!, locatorMarker, providerEntryMarker);
        try
        {
            await WaitForFileAsync(providerEntryMarker, TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(locatorMarker));
            var prepared = await Store(paths).ReadAsync();
            Assert.Equal(2, prepared.RegistryRevision);
            Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorPrepared], prepared.Operations.Select(operation => operation.LifecyclePhase).ToArray());
        }
        finally
        {
            if (!crashHost.HasExited)
            {
                crashHost.Kill(entireProcessTree: true);
            }
            await crashHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.NotEqual(0, crashHost.ExitCode);
    }

    private static CredentialLifecyclePreviewRequest PreviewRequest(string operationId, CredentialLifecycleOperationKind kind, long revision, CredentialContractId? interruptedRepairOperationId = null) => new(Id(operationId), kind, ReferenceId(), "workspace-1", Environment.UserName, revision, interruptedRepairOperationId);

    private static CredentialLifecycleRequest Request(string operationId, CredentialLifecycleOperationKind kind, long revision, CredentialLifecyclePreview preview, CredentialContractId? interruptedRepairOperationId = null) => new(kind, Id(operationId), ReferenceId(), "workspace-1", Environment.UserName, revision, _timestamp, Preview: preview, Confirmed: true, InterruptedRepairOperationId: interruptedRepairOperationId);

    private static CredentialLifecycleRequest CreateRequest(string operationId)
    {
        var reference = CreateReference();
        return new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id(operationId), reference.Id, "workspace-1", Environment.UserName, 0, _timestamp, 4, reference, CreateBinding(), Id("restart-create-consent"));
    }

    private static CredentialRegistryStore Store(WorkspacePaths paths) => new(paths, TestTrust(paths), CredentialLifecyclePersistenceTestAdapter.Instance);

    private static FileCapabilityCatalogTrustProvider TestTrust(WorkspacePaths paths)
    {
        var workspaceRoot = new DirectoryInfo(paths.WorkspacePath);
        var temporaryRoot = workspaceRoot.Parent?.Parent ?? throw new InvalidOperationException("The test workspace root is invalid.");
        return new FileCapabilityCatalogTrustProvider(Path.Combine(temporaryRoot.FullName, "embodysense-test-server-state", workspaceRoot.Name, "credential-lifecycle-restart-trust"));
    }

    private static CredentialReference Reference()
    {
        var binding = Binding();
        return new CredentialReference(1, ReferenceId(), "api-token", CredentialLifecycleStatus.Active, "user-1", "Exercise persisted credential repair recovery.", ProviderId(binding.Implementation.ProviderId.Value), _timestamp, _timestamp, null, new Dictionary<string, string> { ["service"] = "Example" });
    }

    private static CredentialReference CreateReference()
    {
        var binding = CreateBinding();
        return new CredentialReference(1, CreateReferenceId(), "api-token", CredentialLifecycleStatus.Active, Environment.UserName, "Exercise persisted create recovery.", ProviderId(binding.Implementation.ProviderId.Value), _timestamp, _timestamp, null, new Dictionary<string, string> { ["service"] = "example" });
    }

    private static CredentialCapabilityBinding Binding()
    {
        var descriptor = CapabilityCatalogTestData.Descriptor();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        Assert.True(CapabilitySecretRequirement.TryParse("provider-token", out var requirement, out _));
        var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 1, "node-1", identity, descriptor.Implementation, "example", "target", "read", "user-1", null, null);
        return new CredentialCapabilityBinding(1, ReferenceId(), requirement!, identity!, descriptor.Implementation, scope);
    }

    private static CredentialCapabilityBinding CreateBinding()
    {
        Assert.True(CapabilityId.TryParse("org.example/create", out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var capabilityVersion, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var capabilityHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var capabilityProviderId, out _));
        Assert.True(CapabilitySecretRequirement.TryParse("api_token", out var requirement, out _));
        var identity = new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, capabilityHash!);
        var implementation = new CapabilityImplementationIdentity(capabilityProviderId!, "create-provider");
        var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 1, "node-1", identity, implementation, "example", "target", "write", Environment.UserName, null, null);
        return new CredentialCapabilityBinding(1, CreateReferenceId(), requirement!, identity, implementation, scope);
    }

    private static CredentialReferenceId ReferenceId() => CredentialReferenceId.TryParse("credential-restart", out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialReferenceId CreateReferenceId() => CredentialReferenceId.TryParse("credential-create-restart", out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialProviderId ProviderId(string value) => CredentialProviderId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialContractId Id(string value) => CredentialContractId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialProviderLocator Locator() => CredentialProviderLocator.TryParse("loc_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", out var parsed) ? parsed! : throw new InvalidOperationException();
    private static string Hash(char value) => "sha256:" + new string(value, 64);
    private static CredentialLifecycleAuditPayload IntentAuditPayload() => new(AuditSchema.Actions.CredentialLifecycleIntent, AuditSchema.Outcomes.Started, "Credential lifecycle intent durably recorded.");
    private static int Fill(Span<byte> destination, byte value)
    {
        destination.Fill(value);
        return destination.Length;
    }
}
