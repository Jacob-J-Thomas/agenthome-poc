using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.ContextualRoles.Models;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.CancellationHost.Credentials;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

if (args is ["capability", var behavior])
{
    return await HostCapabilityAsync(behavior);
}

if (args is ["hold-contextual-role", var contextualRoleWorkspaceRoot])
{
    return await HoldContextualRoleMutationAsync(contextualRoleWorkspaceRoot);
}

if (args is ["hold-control", var workspaceRoot, var kindText, var runId, var versionText, var operationId])
{
    return await HoldControlOperationAsync(workspaceRoot, kindText, runId, versionText, operationId);
}

if (args is ["credential-repair-crash", var credentialWorkspaceRoot, var crashWindow, var repairProviderEntryMarker, var providerSuccessMarker])
{
    return await CrashCredentialRepairAsync(credentialWorkspaceRoot, crashWindow, repairProviderEntryMarker, providerSuccessMarker);
}

if (args is ["credential-create-crash", var createWorkspaceRoot, var locatorMarker, var providerEntryMarker])
{
    return await CrashCredentialCreateAsync(createWorkspaceRoot, locatorMarker, providerEntryMarker);
}

if (args is ["credential-create-payload-crash", var payloadWorkspaceRoot, var trustProfile, var payloadOperationId, var consentId, var referencePayload, var bindingPayload, var payloadLocatorMarker, var payloadProviderEntryMarker])
{
    return await CrashCredentialPayloadCreateAsync(payloadWorkspaceRoot, trustProfile, payloadOperationId, consentId, referencePayload, bindingPayload, payloadLocatorMarker, payloadProviderEntryMarker);
}

if (args is [var cancellationWorkspaceRoot, var cancellationRunId])
{
    return await HostCancellationAsync(cancellationWorkspaceRoot, cancellationRunId);
}

return 2;

static async Task<int> CrashCredentialRepairAsync(string workspaceRoot, string crashWindow, string providerEntryMarker, string providerSuccessMarker)
{
    var paths = new WorkspacePaths(workspaceRoot);
    var workspaceDirectory = new DirectoryInfo(paths.WorkspacePath);
    var temporaryRoot = workspaceDirectory.Parent?.Parent ?? throw new InvalidOperationException("The test workspace root is invalid.");
    var trustProvider = new FileCapabilityCatalogTrustProvider(Path.Combine(temporaryRoot.FullName, "embodysense-test-server-state", workspaceDirectory.Name, "credential-lifecycle-restart-trust"));
    var adapter = CredentialRepairCrashTestAdapter.Instance;
    var dependentIndex = new CapabilityDependentIndex([adapter]);
    var markProviderSuccess = string.Equals(crashWindow, "AfterProviderSuccess", StringComparison.Ordinal);
    var service = CredentialLifecyclePersistenceFactory.Create(paths, trustProvider, adapter, new CredentialRepairCrashValueProvider(markProviderSuccess, providerEntryMarker, providerSuccessMarker), adapter, dependentIndex, adapter, new AuditLog(paths));
    var operationId = ParseId("restart-repair");
    var referenceId = ParseReferenceId("credential-restart");
    var preview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(operationId, CredentialLifecycleOperationKind.Repair, referenceId, "workspace-1", Environment.UserName, 2));
    if (preview.Status != CredentialLifecyclePreviewStatus.Ready)
    {
        return 3;
    }

    var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, operationId, referenceId, "workspace-1", Environment.UserName, 2, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: preview, Confirmed: true);
    _ = await service.ExecuteAsync(request);
    return 4;
}

static async Task<int> CrashCredentialCreateAsync(string workspaceRoot, string locatorMarker, string providerEntryMarker)
{
    var paths = new WorkspacePaths(workspaceRoot);
    var workspaceDirectory = new DirectoryInfo(paths.WorkspacePath);
    var temporaryRoot = workspaceDirectory.Parent?.Parent ?? throw new InvalidOperationException("The test workspace root is invalid.");
    var trustProvider = new FileCapabilityCatalogTrustProvider(Path.Combine(temporaryRoot.FullName, "embodysense-test-server-state", workspaceDirectory.Name, "credential-lifecycle-restart-trust"));
    var adapter = new CredentialCreateCrashTestAdapter(locatorMarker);
    var dependentIndex = new CapabilityDependentIndex([adapter]);
    var service = CredentialLifecyclePersistenceFactory.Create(paths, trustProvider, adapter, new CredentialCreateCrashValueProvider(providerEntryMarker), adapter, dependentIndex, adapter, new AuditLog(paths));
    _ = await service.ExecuteAsync(CreateRequest("restart-create"), destination =>
    {
        destination.Fill(1);
        return destination.Length;
    });
    return 4;
}

static async Task<int> CrashCredentialPayloadCreateAsync(string workspaceRoot, string trustProfile, string operationId, string consentId, string referencePayload, string bindingPayload, string locatorMarker, string providerEntryMarker)
{
    var trustDirectoryName = trustProfile switch
    {
        "registry" => "credential-registry-trust",
        "restart" => "credential-lifecycle-restart-trust",
        _ => null
    };
    if (trustDirectoryName is null)
    {
        return 2;
    }

    string referenceJson;
    string bindingJson;
    try
    {
        referenceJson = Encoding.UTF8.GetString(Convert.FromBase64String(referencePayload));
        bindingJson = Encoding.UTF8.GetString(Convert.FromBase64String(bindingPayload));
    }
    catch (FormatException)
    {
        return 2;
    }
    if (!CredentialContractJson.TryDeserializeReference(referenceJson, out var reference, out _) || !CredentialContractJson.TryDeserializeBinding(bindingJson, out var binding, out _) || !reference!.Id.Equals(binding!.ReferenceId))
    {
        return 2;
    }

    var paths = new WorkspacePaths(workspaceRoot);
    var workspaceDirectory = new DirectoryInfo(paths.WorkspacePath);
    var temporaryRoot = workspaceDirectory.Parent?.Parent ?? throw new InvalidOperationException("The test workspace root is invalid.");
    var trustProvider = new FileCapabilityCatalogTrustProvider(Path.Combine(temporaryRoot.FullName, "embodysense-test-server-state", workspaceDirectory.Name, trustDirectoryName));
    var adapter = new CredentialCreateCrashTestAdapter(locatorMarker);
    var dependentIndex = new CapabilityDependentIndex([adapter]);
    var service = CredentialLifecyclePersistenceFactory.Create(paths, trustProvider, adapter, new CredentialCreateCrashValueProvider(providerEntryMarker), adapter, dependentIndex, adapter, new AuditLog(paths));
    var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, ParseId(operationId), reference.Id, binding.Scope.WorkspaceId!, Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, ParseId(consentId));
    _ = await service.ExecuteAsync(request, destination =>
    {
        destination.Fill(1);
        return destination.Length;
    });
    return 4;
}

static CredentialContractId ParseId(string value) => CredentialContractId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();

static CredentialReferenceId ParseReferenceId(string value) => CredentialReferenceId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();

static CredentialLifecycleRequest CreateRequest(string operationId)
{
    var reference = CreateReference();
    return new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, ParseId(operationId), reference.Id, "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, CreateBinding(), ParseId("restart-create-consent"));
}

static CredentialReference CreateReference() => new(1, ParseReferenceId("credential-create-restart"), "api-token", CredentialLifecycleStatus.Active, Environment.UserName, "Exercise persisted create recovery.", ParseProviderId("org.example"), new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), null, new Dictionary<string, string> { ["service"] = "example" });

static CredentialCapabilityBinding CreateBinding()
{
    var capability = new CapabilityDescriptorIdentity(ParseCapabilityId("org.example/create"), ParseCapabilityVersion("1.0.0"), ParseCapabilityHash("sha256:" + new string('a', 64)));
    var implementation = new CapabilityImplementationIdentity(ParseCapabilityProviderId("org.example"), "create-provider");
    var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 1, "node-1", capability, implementation, "example", "target", "write", Environment.UserName, null, null);
    return new CredentialCapabilityBinding(1, ParseReferenceId("credential-create-restart"), ParseRequirement("api_token"), capability, implementation, scope);
}

static CredentialProviderId ParseProviderId(string value) => CredentialProviderId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
static CapabilityId ParseCapabilityId(string value) => CapabilityId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
static CapabilityVersion ParseCapabilityVersion(string value) => CapabilityVersion.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
static CapabilityDescriptorHash ParseCapabilityHash(string value) => CapabilityDescriptorHash.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
static CapabilityProviderId ParseCapabilityProviderId(string value) => CapabilityProviderId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
static CapabilitySecretRequirement ParseRequirement(string value) => CapabilitySecretRequirement.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();

static async Task<int> HostCapabilityAsync(string behavior)
{
    var input = await Console.In.ReadLineAsync() ?? "null";
    switch (behavior)
    {
        case "echo":
            Console.Write(input);
            return 0;
        case "malformed":
            Console.Write("not-json");
            return 0;
        case "crash":
            Console.Error.Write("password=hunter2 C:\\private\\secret.txt");
            return 7;
        case "hang":
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        case "oversize":
            Console.Write(new string('x', 128 * 1024));
            return 0;
        case "stderr-oversize":
            Console.Error.Write(new string('x', 128 * 1024));
            return 0;
        case "environment":
            Console.Write(JsonSerializer.Serialize(Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(value => value.ToString()).OrderBy(value => value, StringComparer.Ordinal)));
            return 0;
        case "working-root":
            Console.Write(JsonSerializer.Serialize(Environment.CurrentDirectory));
            return 0;
        default:
            return 2;
    }
}

static async Task<int> HoldContextualRoleMutationAsync(string workspaceRoot)
{
    var now = DateTimeOffset.UtcNow;
    var revision = ContextualRoleRevisionContentHash.Apply(new ContextualRoleRevision(
        1,
        new ContextualRoleRevisionIdentity("reviewer", 1),
        string.Empty,
        "Reviewer",
        "Provide bounded review assistance.",
        ContextualRoleStatus.Published,
        new ContextualRoleProvenance("user-jake", now, now),
        new ContextualRoleWorkspaceApplicability(ImmutableArray.Create("workspace-one")),
        new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.RoleArtifact, "reviewer-source", ContextualRoleInstructionClassification.RoleInstruction),
        new ContextualRolePolicyMaxima(ImmutableArray<string>.Empty)));
    var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("create-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Create, "reviewer", "user-jake", revision, null, now));
    var options = new ContextualRoleRevisionStoreOptions
    {
        DurableBoundaryObserver = async (boundary, _) =>
        {
            if (boundary == ContextualRolePersistenceBoundary.IntentPublished)
            {
                Console.WriteLine("ready");
                await Console.Out.FlushAsync();
                Console.ReadLine();
            }
        }
    };
    var result = await new ContextualRoleRevisionStore(new WorkspacePaths(workspaceRoot), "workspace-one", options).MutateAsync(request);
    return result.Status == ContextualRoleRevisionMutationStatus.Accepted ? 0 : 3;
}

static async Task<int> HostCancellationAsync(string workspaceRoot, string runId)
{
    var paths = new WorkspacePaths(workspaceRoot);
    await using var gate = new CustomLoopWorkspaceExecutionGate(paths);
    using var cancellation = new CancellationTokenSource();
    using var registration = gate.RegisterActiveAttempt(runId, cancellation);
    Console.WriteLine("ready");
    await Console.Out.FlushAsync();
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
    }
    catch (OperationCanceledException exception)
    {
        _ = registration.TryConfirmProviderInterruption(exception.CancellationToken);
        Console.WriteLine("interrupted");
        await Console.Out.FlushAsync();
    }

    _ = Console.ReadLine();
    return 0;
}

static async Task<int> HoldControlOperationAsync(string workspaceRoot, string kindText, string runId, string versionText, string operationId)
{
    if (!Enum.TryParse<CustomLoopControlKind>(kindText, ignoreCase: true, out var kind) || kind == CustomLoopControlKind.Unknown || !int.TryParse(versionText, out var expectedVersion))
    {
        return 2;
    }

    var now = DateTimeOffset.UtcNow.ToUniversalTime();
    var actor = AuditSchema.Actors.Web;
    var pending = new CustomLoopControlOperation(
        CustomLoopControlOperation.CurrentSchemaVersion,
        operationId,
        CustomLoopControlRequestHash.Compute(kind, runId, expectedVersion, operationId, actor),
        kind,
        runId,
        expectedVersion,
        actor,
        now,
        now,
        CustomLoopControlOperationState.Pending,
        CustomLoopControlStatus.Unknown,
        null,
        null,
        false,
        "The child process is holding the pre-transition control-operation execution lease.");
    var result = await new CustomLoopControlOperationStore(new WorkspacePaths(workspaceRoot)).BeginAsync(pending);
    using var lease = result.Lease;
    if (result.Status != CustomLoopControlOperationStoreStatus.Created || lease is null)
    {
        return 3;
    }

    Console.WriteLine("ready");
    await Console.Out.FlushAsync();
    _ = Console.ReadLine();
    return 0;
}
