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
using EmbodySense.CancellationHost.CodexAppServer;
using EmbodySense.CancellationHost.Persistence;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

if (args is ["custom-loop-run-stage", var runLockPath, var runStagingPath, var runReadyMarker, var runReleaseMarker])
{
    return await CustomLoopRunStagingWriterHost.RunAsync(runLockPath, runStagingPath, runReadyMarker, runReleaseMarker);
}

if (args is ["codex-runtime-probe", var probeConfigurationPath, .. var probeArguments])
{
    return await CodexRuntimeProbeHost.RunAsync(probeConfigurationPath, probeArguments);
}

if (args is ["codex-conversation-probe", var conversationConfigurationPath, .. var conversationArguments])
{
    return await CodexConversationProbeHost.RunAsync(conversationConfigurationPath, conversationArguments);
}

if (args is ["credential-mutex-contention", var contentionId])
{
    return await WindowsCredentialProviderCrossProcessHost.RunMutexContentionAsync(contentionId);
}

if (args is ["credential-external-value"])
{
    return await WindowsCredentialProviderCrossProcessHost.RunExternalValueAsync();
}

if (args is ["credential-lease-attempt", var credentialLeasePhase, var credentialLeaseWorkspaceRoot])
{
    return await CredentialLeaseAttemptCrossProcessHost.RunAsync(credentialLeasePhase, credentialLeaseWorkspaceRoot);
}

if (args is ["authority-grant-store", var authorityMode, var authorityWorkspaceRoot, var authorityTrustRoot, var authorityMarkerPath, var authorityResultPath])
{
    return await AuthorityGrantStoreCrossProcessHost.RunAsync(authorityMode, authorityWorkspaceRoot, authorityTrustRoot, authorityMarkerPath, authorityResultPath);
}

if (args is ["default-turn-archive-process-loss", var archiveWorkspaceRoot, var archivePhase])
{
    return await DefaultConversationStoreCrossProcessHost.RunArchiveProcessLossAsync(archiveWorkspaceRoot, archivePhase);
}

if (args is ["default-turn-process-loss", var turnWorkspaceRoot, var turnBoundary])
{
    return await DefaultConversationTurnProcessLossHost.RunAsync(turnWorkspaceRoot, turnBoundary);
}

if (args is ["default-turn-publication", var publicationWorkspaceRoot, var publicationReadyPath, var publicationReleasePath, var publicationResultPath])
{
    return await DefaultConversationStoreCrossProcessHost.RunPublicationAsync(publicationWorkspaceRoot, publicationReadyPath, publicationReleasePath, publicationResultPath);
}

if (args is ["default-turn-active-set-lease", var leaseWorkspaceRoot, var leaseReadyPath, var leaseReleasePath])
{
    return await DefaultConversationStoreCrossProcessHost.RunActiveSetLeaseAsync(leaseWorkspaceRoot, leaseReadyPath, leaseReleasePath);
}

if (args is ["default-turn-history-stage-substitution", var stagePath, var displacedPath, var replacementPayload])
{
    return DefaultConversationStoreCrossProcessHost.RunHistoryStageSubstitution(stagePath, displacedPath, replacementPayload);
}

if (args is ["human-input-request-store", var humanInputMode, var humanInputWorkspaceRoot, var humanInputTrustRoot, var humanInputGatePath, var humanInputReadyPath, var humanInputOutputPath, var humanInputRequestId, var humanInputOperationId, var humanInputRequestHash, var humanInputBoundary, var humanInputGeneration, var humanInputRelatedRequestId])
{
    return await HumanInputRequestStoreCrossProcessHost.RunAsync(humanInputMode, humanInputWorkspaceRoot, humanInputTrustRoot, humanInputGatePath, humanInputReadyPath, humanInputOutputPath, humanInputRequestId, humanInputOperationId, humanInputRequestHash, humanInputBoundary, humanInputGeneration, humanInputRelatedRequestId);
}

if (args is ["governed-loop-revision-store", var revisionMode, var revisionWorkspaceRoot, var revisionTrustRoot, var revisionGatePath, var revisionReadyPath, var revisionOutputPath, var revisionGraphId, var revisionId, var revisionOperationId, var revisionRequestHash])
{
    return await GovernedLoopRevisionStoreCrossProcessHost.RunAsync(revisionMode, revisionWorkspaceRoot, revisionTrustRoot, revisionGatePath, revisionReadyPath, revisionOutputPath, revisionGraphId, revisionId, revisionOperationId, revisionRequestHash);
}

if (args is ["sequential-audit-record-then-exit", var auditWorkspaceRoot])
{
    return await SequentialAuditCrossProcessHost.RecordThenExitAsync(auditWorkspaceRoot);
}

if (args is ["sequential-evidence-resolve", var evidenceWorkspaceRoot, var evidenceHash, var evidenceResultPath])
{
    return await CustomLoopSequentialEvidenceCrossProcessHost.ResolveAsync(evidenceWorkspaceRoot, evidenceHash, evidenceResultPath);
}

if (args is ["effect-authority-crash", var effectMode, var effectWorkspaceRoot, var effectTrustRoot, var effectReleaseMarker, var effectReadyMarker, var effectOperationId])
{
    return await GovernedLoopEffectAuthorityCrashHost.RunAsync(effectMode, effectWorkspaceRoot, effectTrustRoot, effectReleaseMarker, effectReadyMarker, effectOperationId);
}

if (args is ["trigger-queue-admit", var queueWorkspaceRoot, var queueReleaseMarker, var queueReadyMarker, var queueResultMarker, var deliveryId, var deduplicationId, var loopId, var crashBoundary])
{
    return await TriggerQueueCrossProcessHost.RunAdmissionAsync(queueWorkspaceRoot, queueReleaseMarker, queueReadyMarker, queueResultMarker, deliveryId, deduplicationId, loopId, crashBoundary);
}

if (args is ["trigger-worker-select", var workerWorkspaceRoot, var workerReleaseMarker, var workerReadyMarker, var workerResultMarker, var workerId, var expectedGeneration])
{
    return await TriggerQueueCrossProcessHost.RunWorkerSelectionAsync(workerWorkspaceRoot, workerReleaseMarker, workerReadyMarker, workerResultMarker, workerId, expectedGeneration);
}

if (args is ["trigger-queue-hold-lock", var lockWorkspaceRoot, var lockReleaseMarker, var lockReadyMarker, var lockResultMarker])
{
    return await TriggerQueueCrossProcessHost.RunLockHolderAsync(lockWorkspaceRoot, lockReleaseMarker, lockReadyMarker, lockResultMarker);
}

if (args is ["human-input-response", var responseMode, var responseWorkspaceRoot, var responseTrustRoot, var responseReleaseMarker, var responseReadyMarker, var responseResultMarker, var responseOperationId, var responseId, var responseActorId, var responseActorRoleId, var responseBoundary])
{
    return await HumanInputResponseCrossProcessHost.RunAsync(responseMode, responseWorkspaceRoot, responseTrustRoot, responseReleaseMarker, responseReadyMarker, responseResultMarker, responseOperationId, responseId, responseActorId, responseActorRoleId, responseBoundary);
}

if (args is ["capability", var behavior])
{
    return await HostCapabilityAsync(behavior);
}

if (args is ["command-action", var commandBehavior, .. var commandValues])
{
    return await HostCommandActionAsync(commandBehavior, commandValues);
}

if (args is ["command-action-concurrency", var commandWorkspaceRoot, var commandTemplateHash, var commandMaximumConcurrency, var commandReadyMarker, var commandReleaseMarker])
{
    return await CommandActionConcurrencyGateCrossProcessHost.RunAsync(commandWorkspaceRoot, commandTemplateHash, commandMaximumConcurrency, commandReadyMarker, commandReleaseMarker);
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

static async Task<int> HostCommandActionAsync(string behavior, string[] values)
{
    var input = await Console.In.ReadToEndAsync();
    switch (behavior)
    {
        case "literal":
            Console.Write(JsonSerializer.Serialize(new
            {
                arguments = values,
                environment = Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(value => value.ToString()).OrderBy(value => value, StringComparer.Ordinal),
                input,
            }));
            return 0;
        case "nonzero":
            Console.Error.Write("token=secret-canary C:\\private\\command.txt");
            return 7;
        case "malformed":
            Console.Write("not-json");
            return 0;
        case "invalid-encoding":
            await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xff, 0xfe });
            return 0;
        case "overflow":
            var stdout = Task.Run(() => Console.Out.Write(new string('x', 128 * 1024)));
            var stderr = Task.Run(() => Console.Error.Write(new string('y', 128 * 1024)));
            await Task.WhenAll(stdout, stderr);
            return 0;
        case "unicode-boundary":
            Console.OutputEncoding = new UTF8Encoding(false, true);
            Console.Write(new string('x', 4_095) + "😀");
            return 0;
        case "hang":
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        default:
            return 2;
    }
}

static async Task<int> HoldContextualRoleMutationAsync(string workspaceRoot)
{
    var paths = new WorkspacePaths(workspaceRoot);
    var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
    var now = DateTimeOffset.UtcNow;
    var revision = ContextualRoleRevisionContentHash.Apply(new ContextualRoleRevision(
        1,
        new ContextualRoleRevisionIdentity("reviewer", 1),
        string.Empty,
        "Reviewer",
        "Provide bounded review assistance.",
        ContextualRoleStatus.Published,
        new ContextualRoleProvenance("user-jake", now, now),
        new ContextualRoleWorkspaceApplicability(ImmutableArray.Create(workspaceId)),
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
    var result = await new ContextualRoleRevisionStore(paths, workspaceId, options).MutateAsync(request);
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
