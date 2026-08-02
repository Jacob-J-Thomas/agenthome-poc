using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Application.Tests.Credentials;

public sealed class CredentialLifecycleServiceTests
{
    [Fact]
    public async Task CreateRecordsRepairIntentBeforeProviderAndCommitsValueFreeOutcome()
    {
        using var fixture = new LifecycleFixture();
        var canary = Encoding.UTF8.GetBytes("credential-canary-never-persist");

        var result = await fixture.Service.ExecuteAsync(fixture.CreateRequest("create-1", canary.Length), destination =>
        {
            canary.CopyTo(destination);
            return canary.Length;
        });

        Assert.Equal(CredentialLifecycleResultStatus.Applied, result.Status);
        Assert.Equal(CredentialProviderHealthStatus.Available, result.Health);
        Assert.Equal(1, fixture.Provider.CreateCount);
        var read = await fixture.Registry.ReadAsync();
        Assert.False(Assert.Single(read.Entries).ConsentGranted);
        Assert.Equal([CredentialProviderHealthStatus.NeedsRepair, CredentialProviderHealthStatus.NeedsRepair, CredentialProviderHealthStatus.Available], fixture.MutationHealth);
        Assert.DoesNotContain("credential-canary", string.Join('\n', fixture.Audit.Events.Select(auditEvent => auditEvent.ToString())));
    }

    [Fact]
    public async Task ExactCreateReplayDoesNotInvokeProviderAgain()
    {
        using var fixture = new LifecycleFixture();
        var request = fixture.CreateRequest("create-replay", 4);
        await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 7));

        var replay = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 9));

        Assert.Equal(CredentialLifecycleResultStatus.Replayed, replay.Status);
        Assert.Equal(1, fixture.Provider.CreateCount);
    }

    [Fact]
    public async Task UncertainReplaceStaysRepairRequiredAcrossRetry()
    {
        using var fixture = new LifecycleFixture();
        await fixture.Service.ExecuteAsync(fixture.CreateRequest("create-for-replace", 4), destination => Fill(destination, 1));
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("replace-1", CredentialLifecycleOperationKind.Replace, fixture.Registry.Revision));
        fixture.Provider.NextReplaceFailure = CredentialFailureCode.OutcomeUncertain;
        var request = fixture.DestructiveRequest("replace-1", CredentialLifecycleOperationKind.Replace, fixture.Registry.Revision, preview, 4);

        var result = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 2));
        var retry = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 3));

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, retry.Status);
        Assert.Equal(1, fixture.Provider.ReplaceCount);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, Assert.Single((await fixture.Registry.ReadAsync()).Entries).Health);
    }

    [Fact]
    public async Task CancellationAfterCreateEffectCommitsUncertainOutcomeAndNeverRetriesAutomatically()
    {
        using var fixture = new LifecycleFixture();
        fixture.Provider.CancelAfterNextCreateEffect = true;
        var request = fixture.CreateRequest("cancel-create", 4);

        var result = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 1));
        var replay = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 2));

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, replay.Status);
        Assert.Equal(1, fixture.Provider.CreateCount);
        var read = await fixture.Registry.ReadAsync();
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, Assert.Single(read.Entries).Health);
        Assert.Contains(read.Operations, operation => operation.LifecyclePhase == CredentialLifecycleMutationPhase.Uncertain);
    }

    [Fact]
    public async Task CancellationAfterReplaceEffectCommitsUncertainOutcomeAndNeverRetriesAutomatically()
    {
        using var fixture = await CreatedFixtureAsync("cancel-replace-create");
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("cancel-replace", CredentialLifecycleOperationKind.Replace, fixture.Registry.Revision));
        var request = fixture.DestructiveRequest("cancel-replace", CredentialLifecycleOperationKind.Replace, fixture.Registry.Revision, preview, 4);
        fixture.Provider.CancelAfterNextReplaceEffect = true;

        var result = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 2));
        var replay = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 3));

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, replay.Status);
        Assert.Equal(1, fixture.Provider.ReplaceCount);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, Assert.Single((await fixture.Registry.ReadAsync()).Entries).Health);
    }

    [Fact]
    public async Task CancellationAfterDeleteEffectCommitsRepairableTombstoneAndNeverRetriesAutomatically()
    {
        using var fixture = await CreatedFixtureAsync("cancel-delete-create");
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("cancel-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision));
        var request = fixture.DestructiveRequest("cancel-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision, preview);
        fixture.Provider.CancelAfterNextDeleteEffect = true;

        var result = await fixture.Service.ExecuteAsync(request);
        var replay = await fixture.Service.ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, replay.Status);
        Assert.Equal(1, fixture.Provider.DeleteCount);
        Assert.True(Assert.Single((await fixture.Registry.ReadAsync()).Tombstones).NeedsRepair);
    }

    [Fact]
    public async Task CancellationAfterRepairEffectRetainsUnresolvedProjectionAndNeverRetriesAutomatically()
    {
        using var fixture = await CreatedFixtureAsync("cancel-repair-create");
        fixture.Provider.NextDeleteFailure = CredentialFailureCode.OutcomeUncertain;
        var deletePreview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("cancel-repair-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision));
        await fixture.Service.ExecuteAsync(fixture.DestructiveRequest("cancel-repair-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision, deletePreview));
        var repairPreview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("cancel-repair", CredentialLifecycleOperationKind.Repair, fixture.Registry.Revision));
        var request = fixture.DestructiveRequest("cancel-repair", CredentialLifecycleOperationKind.Repair, fixture.Registry.Revision, repairPreview);
        fixture.Provider.CancelAfterNextDeleteEffect = true;

        var result = await fixture.Service.ExecuteAsync(request);
        var replay = await fixture.Service.ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, replay.Status);
        Assert.Equal(2, fixture.Provider.DeleteCount);
        Assert.True(Assert.Single((await fixture.Registry.ReadAsync()).Tombstones).NeedsRepair);
    }

    [Fact]
    public async Task UncertainReplaceBlocksAvailableHealthTestWithoutProviderQueryOrMutation()
    {
        using var fixture = await CreatedFixtureAsync("blocked-test-create");
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("blocked-test-replace", CredentialLifecycleOperationKind.Replace, fixture.Registry.Revision));
        fixture.Provider.NextReplaceFailure = CredentialFailureCode.OutcomeUncertain;
        await fixture.Service.ExecuteAsync(fixture.DestructiveRequest("blocked-test-replace", CredentialLifecycleOperationKind.Replace, fixture.Registry.Revision, preview, 4), destination => Fill(destination, 2));
        var revision = fixture.Registry.Revision;
        var mutationCount = fixture.Registry.Mutations.Count;
        var test = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id("blocked-test"), fixture.Reference.Id, "workspace-1", fixture.ActorId, revision, _timestamp);

        var result = await fixture.Service.ExecuteAsync(test);

        Assert.Equal(CredentialLifecycleResultStatus.Conflict, result.Status);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, result.Health);
        Assert.Equal(0, fixture.Provider.HealthCount);
        Assert.Equal(mutationCount, fixture.Registry.Mutations.Count);
        Assert.Equal(revision, fixture.Registry.Revision);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, Assert.Single((await fixture.Registry.ReadAsync()).Entries).Health);
    }

    [Fact]
    public async Task ChangedDependentSetRejectsConfirmedPreviewBeforeProviderMutation()
    {
        using var fixture = new LifecycleFixture();
        await fixture.Service.ExecuteAsync(fixture.CreateRequest("create-for-stale", 4), destination => Fill(destination, 1));
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("rotate-stale", CredentialLifecycleOperationKind.Rotate, fixture.Registry.Revision));
        fixture.Dependents.Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, Hash('b'), [], "changed");

        var result = await fixture.Service.ExecuteAsync(fixture.DestructiveRequest("rotate-stale", CredentialLifecycleOperationKind.Rotate, fixture.Registry.Revision, preview, 4), destination => Fill(destination, 2));

        Assert.Equal(CredentialLifecycleResultStatus.Conflict, result.Status);
        Assert.Equal(0, fixture.Provider.ReplaceCount);
    }

    [Fact]
    public async Task RevokePublishesImmediatePostureAndAffectedActiveRuns()
    {
        using var fixture = new LifecycleFixture();
        await fixture.Service.ExecuteAsync(fixture.CreateRequest("create-for-revoke", 4), destination => Fill(destination, 1));
        fixture.ActiveRuns.Runs = ["run-2", "run-1"];
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("revoke-1", CredentialLifecycleOperationKind.Revoke, fixture.Registry.Revision));

        var result = await fixture.Service.ExecuteAsync(fixture.DestructiveRequest("revoke-1", CredentialLifecycleOperationKind.Revoke, fixture.Registry.Revision, preview));
        fixture.ActiveRuns.Runs = ["run-3"];
        var replay = await fixture.Service.ExecuteAsync(fixture.DestructiveRequest("revoke-1", CredentialLifecycleOperationKind.Revoke, preview.RegistryRevision!.Value, preview));

        Assert.Equal(CredentialLifecycleResultStatus.Applied, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, replay.Status);
        Assert.Equal(CredentialProviderHealthStatus.Revoked, result.Health);
        Assert.Equal(["run-1", "run-2"], result.AffectedActiveRuns);
        Assert.Equal(["run-1", "run-2"], replay.AffectedActiveRuns);
        Assert.Equal(CredentialLifecycleStatus.Revoked, Assert.Single((await fixture.Registry.ReadAsync()).Entries).Reference.Status);
    }

    [Fact]
    public async Task TestUsesOnlySafeHealthAndNeverConsumesCredentialMaterial()
    {
        using var fixture = new LifecycleFixture();
        await fixture.Service.ExecuteAsync(fixture.CreateRequest("create-for-test", 4), destination => Fill(destination, 1));
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id("test-1"), fixture.Reference.Id, "workspace-1", fixture.ActorId, fixture.Registry.Revision, _timestamp);

        var result = await fixture.Service.ExecuteAsync(request);
        var replay = await fixture.Service.ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.Applied, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, replay.Status);
        Assert.Equal(1, fixture.Provider.HealthCount);
        Assert.Equal(0, fixture.Provider.UseCount);
    }

    [Fact]
    public async Task SuccessfulDeleteReplaysFromTombstoneWithoutCallingProviderAgain()
    {
        using var fixture = new LifecycleFixture();
        await fixture.Service.ExecuteAsync(fixture.CreateRequest("create-for-delete", 4), destination => Fill(destination, 1));
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("delete-1", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision));
        var request = fixture.DestructiveRequest("delete-1", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision, preview);

        var result = await fixture.Service.ExecuteAsync(request);
        var replay = await fixture.Service.ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.Applied, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, replay.Status);
        Assert.Equal(1, fixture.Provider.DeleteCount);
        Assert.Empty((await fixture.Registry.ReadAsync()).Entries);
        Assert.Single((await fixture.Registry.ReadAsync()).Tombstones);
    }

    [Fact]
    public async Task ChangedValueFreeIntentCannotReuseCreateOperationIdentity()
    {
        using var fixture = new LifecycleFixture();
        var original = fixture.CreateRequest("create-conflict", 4);
        await fixture.Service.ExecuteAsync(original, destination => Fill(destination, 1));
        var changed = original with { ValueByteLength = 5 };

        var result = await fixture.Service.ExecuteAsync(changed, destination => Fill(destination, 2));

        Assert.Equal(CredentialLifecycleResultStatus.Conflict, result.Status);
        Assert.Equal(1, fixture.Provider.CreateCount);
    }

    [Fact]
    public async Task ConsentRequiresAuthenticatedUserAndDoesNotChangeProvider()
    {
        using var fixture = new LifecycleFixture();
        await fixture.Service.ExecuteAsync(fixture.CreateRequest("create-for-consent", 4), destination => Fill(destination, 1));
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Consent, Id("consent-1"), fixture.Reference.Id, "workspace-1", "agent-1", fixture.Registry.Revision, _timestamp, ConsentReference: Id("consent-document-1"));

        var result = await fixture.Service.ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.Denied, result.Status);
        Assert.False(Assert.Single((await fixture.Registry.ReadAsync()).Entries).ConsentGranted);
        Assert.Equal(1, fixture.Provider.CreateCount);
        Assert.Equal("agent-1", Assert.Single(fixture.Audit.Events, auditEvent => auditEvent.Action == "credential.lifecycle.outcome" && Equals(auditEvent.Metadata["operationId"], "consent-1")).Metadata["actorId"]);
    }

    [Fact]
    public async Task PreviewReturnsClosedInvalidDeniedConflictNotFoundAndUnavailableOutcomes()
    {
        using var invalid = new LifecycleFixture();
        Assert.Equal(CredentialLifecyclePreviewStatus.Invalid, (await invalid.Service.PreviewAsync(invalid.PreviewRequest("preview-invalid", CredentialLifecycleOperationKind.Test, 0))).Status);
        Assert.Equal(CredentialLifecyclePreviewStatus.Invalid, (await invalid.Service.PreviewAsync(invalid.PreviewRequest("preview-null-reference", CredentialLifecycleOperationKind.Delete, 0) with { ReferenceId = null! })).Status);
        Assert.Equal(CredentialLifecyclePreviewStatus.Invalid, (await invalid.Service.PreviewAsync(invalid.PreviewRequest("preview-unsafe-actor", CredentialLifecycleOperationKind.Delete, 0) with { ActorId = "bad\nactor" })).Status);
        Assert.Equal("invalid", invalid.Audit.Events[^1].Metadata["actorId"]);
        using var denied = new LifecycleFixture();
        Assert.Equal(CredentialLifecyclePreviewStatus.Denied, (await denied.Service.PreviewAsync(denied.PreviewRequest("preview-denied", CredentialLifecycleOperationKind.Delete, 0) with { ActorId = "agent-1" })).Status);
        using var missing = new LifecycleFixture();
        Assert.Equal(CredentialLifecyclePreviewStatus.NotFound, (await missing.Service.PreviewAsync(missing.PreviewRequest("preview-missing", CredentialLifecycleOperationKind.Delete, 0))).Status);
        using var unavailable = new LifecycleFixture();
        unavailable.Registry.MakeUnavailable();
        Assert.Equal(CredentialLifecyclePreviewStatus.Unavailable, (await unavailable.Service.PreviewAsync(unavailable.PreviewRequest("preview-unavailable", CredentialLifecycleOperationKind.Delete, 0))).Status);
        using var conflict = new LifecycleFixture();
        await conflict.Service.ExecuteAsync(conflict.CreateRequest("preview-create", 4), destination => Fill(destination, 1));
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, (await conflict.Service.PreviewAsync(conflict.PreviewRequest("preview-conflict", CredentialLifecycleOperationKind.Delete, 0))).Status);
        conflict.Dependents.Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Unavailable, string.Empty, [], "unavailable");
        Assert.Equal(CredentialLifecyclePreviewStatus.Unavailable, (await conflict.Service.PreviewAsync(conflict.PreviewRequest("preview-dependent", CredentialLifecycleOperationKind.Delete, conflict.Registry.Revision))).Status);
    }

    [Fact]
    public async Task InvalidUnauthenticatedUnavailableStaleAndMissingRequestsFailBeforeMutation()
    {
        using var invalid = new LifecycleFixture();
        Assert.Equal(CredentialLifecycleResultStatus.Invalid, (await invalid.Service.ExecuteAsync(invalid.CreateRequest("invalid-source", 4))).Status);
        using var unauthenticated = new LifecycleFixture();
        Assert.Equal(CredentialLifecycleResultStatus.Denied, (await unauthenticated.Service.ExecuteAsync(unauthenticated.CreateRequest("unauthenticated", 4) with { ActorId = "agent-1" }, destination => Fill(destination, 1))).Status);
        using var unavailable = new LifecycleFixture();
        unavailable.Registry.MakeUnavailable();
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, (await unavailable.Service.ExecuteAsync(unavailable.CreateRequest("unavailable", 4), destination => Fill(destination, 1))).Status);
        using var stale = new LifecycleFixture();
        var staleRequest = stale.CreateRequest("stale", 4) with { ExpectedRegistryRevision = 1 };
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, (await stale.Service.ExecuteAsync(staleRequest, destination => Fill(destination, 1))).Status);
        using var missing = new LifecycleFixture();
        var test = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id("missing-test"), missing.Reference.Id, "workspace-1", missing.ActorId, 0, _timestamp);
        Assert.Equal(CredentialLifecycleResultStatus.NotFound, (await missing.Service.ExecuteAsync(test)).Status);
    }

    [Fact]
    public async Task LocatorFailureRemainsValueFreeAndRepairVisible()
    {
        using var locatorFailure = new LifecycleFixture();
        locatorFailure.LocatorSource.Available = false;
        var unavailable = await locatorFailure.Service.ExecuteAsync(locatorFailure.CreateRequest("locator-failure", 4), destination => Fill(destination, 1));
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, unavailable.Status);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, unavailable.Health);
        Assert.Equal(1, locatorFailure.LocatorSource.CreateCount);
        Assert.Equal(0, locatorFailure.Provider.CreateCount);
    }

    [Fact]
    public async Task PublicPersistenceFailuresAtLifecycleBoundariesRemainValueFreeAndRepairVisible()
    {
        using var createIntent = new LifecycleFixture();
        createIntent.Registry.MakeUnavailable();
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, (await createIntent.Service.ExecuteAsync(createIntent.CreateRequest("create-intent-failure", 4), destination => Fill(destination, 1))).Status);
        Assert.Equal(0, createIntent.Provider.CreateCount);

        using var locatorAttachment = new LifecycleFixture();
        locatorAttachment.LocatorSource.BeforeCreate = locatorAttachment.Registry.MakeUnavailable;
        var attachmentResult = await locatorAttachment.Service.ExecuteAsync(locatorAttachment.CreateRequest("locator-attachment-failure", 4), destination => Fill(destination, 1));
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, attachmentResult.Status);
        Assert.Equal(0, locatorAttachment.Provider.CreateCount);

        using var completion = new LifecycleFixture();
        completion.Provider.BeforeMutation = completion.Registry.MakeUnavailable;
        var completionResult = await completion.Service.ExecuteAsync(completion.CreateRequest("completion-failure", 4), destination => Fill(destination, 1));
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, completionResult.Status);
        Assert.Equal(1, completion.Provider.CreateCount);

        using var rollback = new LifecycleFixture();
        rollback.Provider.BeforeMutation = rollback.Registry.MakeUnavailable;
        var rollbackResult = await rollback.Service.ExecuteAsync(rollback.CreateRequest("rollback-failure", 4), _ => throw new InvalidOperationException("proved callback failure"));
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, rollbackResult.Status);

        using var rotateIntent = await CreatedFixtureAsync("rotate-intent-create");
        var rotatePreview = await rotateIntent.Service.PreviewAsync(rotateIntent.PreviewRequest("rotate-intent-failure", CredentialLifecycleOperationKind.Rotate, rotateIntent.Registry.Revision));
        var rotateRevision = rotateIntent.Registry.Revision;
        rotateIntent.Registry.MakeUnavailable();
        var rotateResult = await rotateIntent.Service.ExecuteAsync(rotateIntent.DestructiveRequest("rotate-intent-failure", CredentialLifecycleOperationKind.Rotate, rotateRevision, rotatePreview, 4), destination => Fill(destination, 2));
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, rotateResult.Status);
        Assert.Equal(0, rotateIntent.Provider.ReplaceCount);

        using var tombstone = await CreatedFixtureAsync("tombstone-failure-create");
        var deletePreview = await tombstone.Service.PreviewAsync(tombstone.PreviewRequest("tombstone-failure", CredentialLifecycleOperationKind.Delete, tombstone.Registry.Revision));
        tombstone.Provider.BeforeMutation = tombstone.Registry.MakeUnavailable;
        var deleteResult = await tombstone.Service.ExecuteAsync(tombstone.DestructiveRequest("tombstone-failure", CredentialLifecycleOperationKind.Delete, tombstone.Registry.Revision, deletePreview));
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, deleteResult.Status);
        Assert.Equal(1, tombstone.Provider.DeleteCount);
    }

    [Fact]
    public async Task ProvedProviderFailureRollsBackAndReplaysWithoutRetry()
    {
        using var fixture = new LifecycleFixture();
        var request = fixture.CreateRequest("provider-failure", 4);
        var failure = await fixture.Service.ExecuteAsync(request, _ => throw new InvalidOperationException("test callback failure"));
        var replay = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 1));

        Assert.Equal(CredentialLifecycleResultStatus.Failed, failure.Status);
        Assert.Equal(CredentialProviderHealthStatus.Missing, failure.Health);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, replay.Status);
        Assert.Equal(1, fixture.Provider.CreateCount);
    }

    [Fact]
    public async Task BindConsentExpireAndDisableHaveExplicitMetadataOnlyTransitions()
    {
        using var bindFixture = await CreatedFixtureAsync("metadata-bind-create");
        var bind = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Bind, Id("bind-1"), bindFixture.Reference.Id, "workspace-1", bindFixture.ActorId, bindFixture.Registry.Revision, _timestamp, Binding: bindFixture.Binding);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await bindFixture.Service.ExecuteAsync(bind)).Status);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, (await bindFixture.Service.ExecuteAsync(bind)).Status);

        using var consentFixture = await CreatedFixtureAsync("metadata-consent-create");
        var consent = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Consent, Id("consent-success"), consentFixture.Reference.Id, "workspace-1", consentFixture.ActorId, consentFixture.Registry.Revision, _timestamp, ConsentReference: Id("consent-document"));
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await consentFixture.Service.ExecuteAsync(consent)).Status);
        Assert.True(Assert.Single((await consentFixture.Registry.ReadAsync()).Entries).ConsentGranted);

        using var expireFixture = await CreatedFixtureAsync("metadata-expire-create");
        var expirePreview = await expireFixture.Service.PreviewAsync(expireFixture.PreviewRequest("expire-1", CredentialLifecycleOperationKind.Expire, expireFixture.Registry.Revision));
        Assert.Equal(CredentialProviderHealthStatus.Expired, (await expireFixture.Service.ExecuteAsync(expireFixture.DestructiveRequest("expire-1", CredentialLifecycleOperationKind.Expire, expireFixture.Registry.Revision, expirePreview))).Health);

        using var disableFixture = await CreatedFixtureAsync("metadata-disable-create");
        disableFixture.ActiveRuns.Failure = new IOException("active run index unavailable");
        var disablePreview = await disableFixture.Service.PreviewAsync(disableFixture.PreviewRequest("disable-1", CredentialLifecycleOperationKind.Disable, disableFixture.Registry.Revision));
        var disabled = await disableFixture.Service.ExecuteAsync(disableFixture.DestructiveRequest("disable-1", CredentialLifecycleOperationKind.Disable, disableFixture.Registry.Revision, disablePreview));
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, disabled.Status);
        Assert.Equal(CredentialProviderHealthStatus.Available, disabled.Health);
        Assert.Empty(disabled.AffectedActiveRuns);
        Assert.Equal(CredentialLifecycleStatus.Active, Assert.Single((await disableFixture.Registry.ReadAsync()).Entries).Reference.Status);
    }

    [Fact]
    public async Task UncertainDeleteStillCommitsDistinctRepairTombstone()
    {
        using var fixture = await CreatedFixtureAsync("uncertain-delete-create");
        fixture.Provider.NextDeleteFailure = CredentialFailureCode.OutcomeUncertain;
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("uncertain-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision));
        var request = fixture.DestructiveRequest("uncertain-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision, preview);

        var result = await fixture.Service.ExecuteAsync(request);
        var replay = await fixture.Service.ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, replay.Status);
        Assert.Single((await fixture.Registry.ReadAsync()).Tombstones);
        Assert.Equal(1, fixture.Provider.DeleteCount);
    }

    [Fact]
    public async Task PreviewProjectsOnlyExactCapabilityDependents()
    {
        using var fixture = await CreatedFixtureAsync("dependent-preview-create");
        Assert.True(CapabilityId.TryParse("org.example/dependent-loop", out var subjectId, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var versionRange, out _));
        var manifest = new CapabilityDependencyManifest(1, CapabilityDependencyManifestKind.LoopPackage, subjectId!, [new CapabilityDependency(fixture.Binding.Capability.Id, versionRange!)], [], new CapabilityDependencyArtifactMetadata(null, null));
        fixture.Dependents.Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, Hash('d'), [new CapabilityDependent(CapabilityDependentKind.Loop, "loop-1", "revision-7", manifest, CapabilityAuthorityPosture.AssignedDefinition)], "available");

        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("dependent-preview", CredentialLifecycleOperationKind.Revoke, fixture.Registry.Revision));

        var impact = Assert.Single(preview.Impacts);
        Assert.Equal(CapabilityDependentKind.Loop, impact.Kind);
        Assert.Equal("loop-1", impact.Identity);
        Assert.Equal("revision-7", impact.Revision);
        Assert.Equal(CapabilityAuthorityPosture.AssignedDefinition, impact.AuthorityPosture);
        Assert.Equal(fixture.ActorId, Assert.Single(fixture.Audit.Events, auditEvent => auditEvent.Action == "credential.lifecycle.preview").Metadata["actorId"]);
    }

    [Fact]
    public async Task InvalidShapesTransitionsAndActiveRunSnapshotsFailClosed()
    {
        using var invalid = new LifecycleFixture();
        var invalidRequest = invalid.CreateRequest("invalid-workspace", 4) with { WorkspaceId = string.Empty };
        Assert.Equal(CredentialLifecycleResultStatus.Invalid, (await invalid.Service.ExecuteAsync(invalidRequest, destination => Fill(destination, 1))).Status);

        using var incompleteBind = await CreatedFixtureAsync("incomplete-bind-create");
        var bind = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Bind, Id("incomplete-bind"), incompleteBind.Reference.Id, "workspace-1", incompleteBind.ActorId, incompleteBind.Registry.Revision, _timestamp);
        Assert.Equal(CredentialLifecycleResultStatus.Invalid, (await incompleteBind.Service.ExecuteAsync(bind)).Status);

        using var invalidRuns = await CreatedFixtureAsync("invalid-runs-create");
        invalidRuns.ActiveRuns.Runs = ["duplicate", "duplicate"];
        var preview = await invalidRuns.Service.PreviewAsync(invalidRuns.PreviewRequest("invalid-runs-disable", CredentialLifecycleOperationKind.Disable, invalidRuns.Registry.Revision));
        var disabled = await invalidRuns.Service.ExecuteAsync(invalidRuns.DestructiveRequest("invalid-runs-disable", CredentialLifecycleOperationKind.Disable, invalidRuns.Registry.Revision, preview));
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, disabled.Status);
        Assert.Empty(disabled.AffectedActiveRuns);

        using var invalidTransition = await CreatedFixtureAsync("invalid-transition-create");
        var disablePreview = await invalidTransition.Service.PreviewAsync(invalidTransition.PreviewRequest("invalid-transition-disable", CredentialLifecycleOperationKind.Disable, invalidTransition.Registry.Revision));
        await invalidTransition.Service.ExecuteAsync(invalidTransition.DestructiveRequest("invalid-transition-disable", CredentialLifecycleOperationKind.Disable, invalidTransition.Registry.Revision, disablePreview));
        var rotatePreview = await invalidTransition.Service.PreviewAsync(invalidTransition.PreviewRequest("invalid-transition-rotate", CredentialLifecycleOperationKind.Rotate, invalidTransition.Registry.Revision));
        var rotate = await invalidTransition.Service.ExecuteAsync(invalidTransition.DestructiveRequest("invalid-transition-rotate", CredentialLifecycleOperationKind.Rotate, invalidTransition.Registry.Revision, rotatePreview, 4), destination => Fill(destination, 2));
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, rotate.Status);
        Assert.Equal(0, invalidTransition.Provider.ReplaceCount);
    }

    [Fact]
    public async Task NullProviderHealthReturnsStructuredUnavailableOutcome()
    {
        using var fixture = await CreatedFixtureAsync("null-health-create");
        fixture.Provider.ReturnNullHealth = true;
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id("null-health"), fixture.Reference.Id, "workspace-1", fixture.ActorId, fixture.Registry.Revision, _timestamp);

        var result = await fixture.Service.ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, result.Status);
        Assert.Equal(CredentialProviderHealthStatus.Unavailable, result.Health);
        Assert.Equal(1, fixture.Provider.HealthCount);
    }

    [Fact]
    public async Task WorkspaceMustMatchBindingForCreatePreviewAndProviderUse()
    {
        using var create = new LifecycleFixture();
        var mismatchedCreate = create.CreateRequest("workspace-create", 4) with { WorkspaceId = "workspace-2" };
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, (await create.Service.ExecuteAsync(mismatchedCreate, destination => Fill(destination, 1))).Status);
        Assert.Equal(0, create.Provider.CreateCount);
        Assert.Equal(0, create.Registry.Revision);

        using var fixture = await CreatedFixtureAsync("workspace-existing-create");
        var mismatchedPreview = fixture.PreviewRequest("workspace-preview", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision) with { WorkspaceId = "workspace-2" };
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, (await fixture.Service.PreviewAsync(mismatchedPreview)).Status);
        var readyPreview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("workspace-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision));
        var crossWorkspaceDelete = fixture.DestructiveRequest("workspace-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision, readyPreview) with { WorkspaceId = "workspace-2" };
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, (await fixture.Service.ExecuteAsync(crossWorkspaceDelete)).Status);
        Assert.Equal(0, fixture.Provider.DeleteCount);
        var test = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id("workspace-test"), fixture.Reference.Id, "workspace-2", fixture.ActorId, fixture.Registry.Revision, _timestamp);
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, (await fixture.Service.ExecuteAsync(test)).Status);
        Assert.Equal(0, fixture.Provider.HealthCount);
    }

    [Theory]
    [InlineData(CredentialLifecycleOperationKind.Revoke, CredentialProviderHealthStatus.Revoked)]
    [InlineData(CredentialLifecycleOperationKind.Disable, CredentialProviderHealthStatus.Disabled)]
    [InlineData(CredentialLifecycleOperationKind.Expire, CredentialProviderHealthStatus.Expired)]
    public async Task TestCannotWidenRestrictivePosture(CredentialLifecycleOperationKind kind, CredentialProviderHealthStatus expectedHealth)
    {
        var operation = kind.ToString().ToLowerInvariant();
        using var fixture = await CreatedFixtureAsync($"restrictive-{operation}-create");
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest($"restrictive-{operation}", kind, fixture.Registry.Revision));
        var restricted = await fixture.Service.ExecuteAsync(fixture.DestructiveRequest($"restrictive-{operation}", kind, fixture.Registry.Revision, preview));
        var test = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id($"restrictive-{operation}-test"), fixture.Reference.Id, "workspace-1", fixture.ActorId, fixture.Registry.Revision, _timestamp);

        var result = await fixture.Service.ExecuteAsync(test);

        Assert.Equal(expectedHealth, restricted.Health);
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, result.Status);
        Assert.Equal(expectedHealth, result.Health);
        Assert.Equal(0, fixture.Provider.HealthCount);
        Assert.Equal(expectedHealth, Assert.Single((await fixture.Registry.ReadAsync()).Entries).Health);
    }

    [Fact]
    public async Task PublicRawDerivedOperationCollisionNeverReplaysAsLifecycleSuccess()
    {
        using var fixture = await CreatedFixtureAsync("collision-replace-create");
        var lifecycleOperationId = Id("collision-replace");
        var derivedCompletionId = DeriveOperationId(lifecycleOperationId, "complete");
        var collision = new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, derivedCompletionId, fixture.Registry.Revision, fixture.Reference.Id, null, null, null, CredentialProviderHealthStatus.Available, null);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await fixture.Registry.MutateAsync(collision)).Status);
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("collision-replace", CredentialLifecycleOperationKind.Replace, fixture.Registry.Revision));
        var request = fixture.DestructiveRequest("collision-replace", CredentialLifecycleOperationKind.Replace, fixture.Registry.Revision, preview, 4);

        var result = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 2));
        var replay = await fixture.Service.ExecuteAsync(request, destination => Fill(destination, 3));

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, replay.Status);
        Assert.Equal(1, fixture.Provider.ReplaceCount);
    }

    [Fact]
    public async Task UncertainDeleteRetainsRepairableTombstoneUntilExplicitConfirmedRepair()
    {
        using var fixture = await CreatedFixtureAsync("repair-delete-create");
        fixture.Provider.NextDeleteFailure = CredentialFailureCode.OutcomeUncertain;
        var deletePreview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("repair-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision));
        var delete = fixture.DestructiveRequest("repair-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision, deletePreview);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await fixture.Service.ExecuteAsync(delete)).Status);
        var retained = Assert.Single((await fixture.Registry.ReadAsync()).Tombstones);
        Assert.True(retained.NeedsRepair);
        Assert.NotNull(retained.RepairBinding);
        Assert.NotNull(retained.RepairProviderId);

        var crossWorkspace = fixture.PreviewRequest("repair-cross-workspace", CredentialLifecycleOperationKind.Repair, fixture.Registry.Revision) with { WorkspaceId = "workspace-2" };
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, (await fixture.Service.PreviewAsync(crossWorkspace)).Status);
        var repairPreview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("repair-cleanup", CredentialLifecycleOperationKind.Repair, fixture.Registry.Revision));
        var forgedPreview = repairPreview with { WorkspaceId = "workspace-2" };
        var forgedRepair = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, Id("repair-cleanup"), fixture.Reference.Id, "workspace-1", fixture.ActorId, fixture.Registry.Revision, _timestamp, Preview: forgedPreview, Confirmed: true);
        var rejected = await fixture.Service.ExecuteAsync(forgedRepair);
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, rejected.Status);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, rejected.Health);

        var repair = forgedRepair with { Preview = repairPreview };
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await fixture.Service.ExecuteAsync(repair)).Status);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, (await fixture.Service.ExecuteAsync(repair)).Status);
        Assert.False(Assert.Single((await fixture.Registry.ReadAsync()).Tombstones).NeedsRepair);
        Assert.Equal(2, fixture.Provider.DeleteCount);
        Assert.Equal(CredentialLifecyclePreviewStatus.NotFound, (await fixture.Service.PreviewAsync(fixture.PreviewRequest("repair-resolved", CredentialLifecycleOperationKind.Repair, fixture.Registry.Revision))).Status);
    }

    [Theory]
    [InlineData(CredentialLifecycleOperationKind.Create, CredentialLifecycleResultStatus.Applied)]
    [InlineData(CredentialLifecycleOperationKind.Replace, CredentialLifecycleResultStatus.Failed)]
    [InlineData(CredentialLifecycleOperationKind.Delete, CredentialLifecycleResultStatus.NeedsRepair)]
    [InlineData(CredentialLifecycleOperationKind.Repair, CredentialLifecycleResultStatus.Applied)]
    public async Task TerminalOutcomeAuditSurvivesSinkFailureAndIsReconciledAfterServiceRestart(CredentialLifecycleOperationKind kind, CredentialLifecycleResultStatus expectedStatus)
    {
        using var fixture = new LifecycleFixture();
        var failingService = fixture.CreateService(new FailingAuditLog());
        CredentialLifecycleResult result;
        if (kind == CredentialLifecycleOperationKind.Create)
        {
            result = await failingService.ExecuteAsync(fixture.CreateRequest("audit-create", 4), destination => Fill(destination, 1));
        }
        else
        {
            await fixture.Service.ExecuteAsync(fixture.CreateRequest($"audit-{kind.ToString().ToLowerInvariant()}-create", 4), destination => Fill(destination, 1));
            if (kind == CredentialLifecycleOperationKind.Repair)
            {
                fixture.Provider.NextDeleteFailure = CredentialFailureCode.OutcomeUncertain;
                var deletePreview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("audit-repair-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision));
                await fixture.Service.ExecuteAsync(fixture.DestructiveRequest("audit-repair-delete", CredentialLifecycleOperationKind.Delete, fixture.Registry.Revision, deletePreview));
            }
            var operation = $"audit-{kind.ToString().ToLowerInvariant()}";
            var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest(operation, kind, fixture.Registry.Revision));
            var request = fixture.DestructiveRequest(operation, kind, fixture.Registry.Revision, preview, kind == CredentialLifecycleOperationKind.Replace ? 4 : 0);
            if (kind == CredentialLifecycleOperationKind.Replace)
            {
                fixture.Provider.NextReplaceFailure = CredentialFailureCode.CallbackFailed;
                result = await failingService.ExecuteAsync(request, destination => Fill(destination, 2));
            }
            else
            {
                if (kind == CredentialLifecycleOperationKind.Delete)
                {
                    fixture.Provider.NextDeleteFailure = CredentialFailureCode.OutcomeUncertain;
                }
                result = await failingService.ExecuteAsync(request);
            }
        }

        Assert.Equal(expectedStatus, result.Status);
        var pending = (await fixture.Registry.ReadAsync()).PendingAudits;
        Assert.Equal(2, pending.Count);
        Assert.Equal("credential.lifecycle.intent", pending[0].Action);
        Assert.Equal("credential.lifecycle.outcome", pending[1].Action);
        Assert.True(pending[0].RegistryRevision < pending[1].RegistryRevision);
        var recoveredAudit = new RecordingCapabilityAuditLog();
        var recoveredService = fixture.CreateService(recoveredAudit);

        var drain = await recoveredService.DrainAuditAsync();

        Assert.Null(drain.Failure);
        Assert.Equal(0, drain.RemainingCount);
        Assert.Equal(2, drain.DeliveredCount);
        Assert.Empty((await fixture.Registry.ReadAsync()).PendingAudits);
        Assert.Equal(["credential.lifecycle.intent", "credential.lifecycle.outcome"], recoveredAudit.Events.Select(item => item.Action).ToArray());
        Assert.All(recoveredAudit.Events, delivered => Assert.Equal(pending[0].LifecycleIntentOperationId.Value, delivered.Metadata["operationId"]));
        Assert.Equal(pending[0].AuditOperationId.Value, recoveredAudit.Events[0].Metadata["auditOperationId"]);
        Assert.Equal(pending[1].AuditOperationId.Value, recoveredAudit.Events[1].Metadata["terminalOperationId"]);
    }

    [Fact]
    public async Task ProviderMutationWaitsForDurableIntentAuditAndRetryDrainsInOrderWithoutRepeatingEffect()
    {
        using var fixture = new LifecycleFixture();
        var request = fixture.CreateRequest("audit-order-retry", 4);
        fixture.LocatorSource.BeforeCreate = () =>
        {
            var durableIntent = Assert.Single(fixture.Registry.ReadAsync().GetAwaiter().GetResult().PendingAudits);
            Assert.Equal(request.OperationId, durableIntent.AuditOperationId);
            Assert.Equal("credential.lifecycle.intent", durableIntent.Action);
        };
        fixture.Provider.BeforeMutation = () =>
        {
            var durableIntent = Assert.Single(fixture.Registry.ReadAsync().GetAwaiter().GetResult().PendingAudits);
            Assert.Equal(request.OperationId, durableIntent.AuditOperationId);
            Assert.Equal(request.OperationId, durableIntent.LifecycleIntentOperationId);
            Assert.Equal("credential.lifecycle.intent", durableIntent.Action);
        };
        var service = fixture.CreateService(new FailingAuditLog());

        var result = await service.ExecuteAsync(request, destination => Fill(destination, 2));

        Assert.Equal(CredentialLifecycleResultStatus.Applied, result.Status);
        Assert.Equal(1, fixture.LocatorSource.CreateCount);
        Assert.Equal(1, fixture.Provider.CreateCount);
        var pending = (await fixture.Registry.ReadAsync()).PendingAudits;
        Assert.Equal(["credential.lifecycle.intent", "credential.lifecycle.outcome"], pending.Select(item => item.Action).ToArray());
        var recoveredAudit = new RecordingCapabilityAuditLog();
        var replay = await fixture.CreateService(recoveredAudit).ExecuteAsync(request, destination => Fill(destination, 3));
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, replay.Status);
        Assert.Equal(1, fixture.Provider.CreateCount);
        Assert.Empty((await fixture.Registry.ReadAsync()).PendingAudits);
        Assert.Equal(["credential.lifecycle.intent", "credential.lifecycle.outcome"], recoveredAudit.Events.Select(item => item.Action).ToArray());
        Assert.All(recoveredAudit.Events, delivered => Assert.Equal(request.OperationId.Value, delivered.Metadata["operationId"]));
    }

    [Fact]
    public async Task AmbiguousLocatorEffectIsTerminalAndNeverRetried()
    {
        using var fixture = new LifecycleFixture();
        var request = fixture.CreateRequest("locator-ambiguous", 4);
        fixture.LocatorSource.CancelAfterNextEffect = true;
        var service = fixture.CreateService(new FailingAuditLog());

        var result = await service.ExecuteAsync(request, destination => Fill(destination, 1));
        var replay = await service.ExecuteAsync(request, destination => Fill(destination, 2));
        var competing = await service.ExecuteAsync(fixture.CreateRequest("locator-competing", 4) with { ExpectedRegistryRevision = fixture.Registry.Revision }, destination => Fill(destination, 3));

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, replay.Status);
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, competing.Status);
        Assert.Equal(1, fixture.LocatorSource.CreateCount);
        Assert.Equal(0, fixture.Provider.CreateCount);
        Assert.Empty((await fixture.Registry.ReadAsync()).Entries);
        var pending = (await fixture.Registry.ReadAsync()).PendingAudits;
        Assert.Equal(["credential.lifecycle.intent", "credential.lifecycle.outcome"], pending.Select(item => item.Action).ToArray());
        Assert.Contains((await fixture.Registry.ReadAsync()).Operations, operation => operation.LifecyclePhase == CredentialLifecycleMutationPhase.LocatorUncertain);
        var recoveredAudit = new RecordingCapabilityAuditLog();
        var drain = await fixture.CreateService(recoveredAudit).DrainAuditAsync();
        Assert.Null(drain.Failure);
        Assert.Equal(2, drain.DeliveredCount);
        Assert.Equal(["credential.lifecycle.intent", "credential.lifecycle.outcome"], recoveredAudit.Events.Select(item => item.Action).ToArray());

        using var exceptionFixture = new LifecycleFixture();
        exceptionFixture.LocatorSource.FailAfterNextEffect = true;
        var exceptionResult = await exceptionFixture.Service.ExecuteAsync(exceptionFixture.CreateRequest("locator-exception", 4), destination => Fill(destination, 4));
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, exceptionResult.Status);
        Assert.Equal(1, exceptionFixture.LocatorSource.CreateCount);
        Assert.Equal(0, exceptionFixture.Provider.CreateCount);
    }

    [Fact]
    public async Task InterruptedRepairIdentityIsAcceptedOnlyByReconciliation()
    {
        using var fixture = new LifecycleFixture();
        var target = Id("repair-target-shape");
        var missingTarget = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.ReconcileRepair, Id("reconcile-missing-target"), fixture.Reference.Id, "workspace-1", fixture.ActorId, 0, _timestamp);
        var unexpectedTarget = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id("test-unexpected-target"), fixture.Reference.Id, "workspace-1", fixture.ActorId, 0, _timestamp, InterruptedRepairOperationId: target);

        Assert.Equal(CredentialLifecycleResultStatus.Invalid, (await fixture.Service.ExecuteAsync(missingTarget)).Status);
        Assert.Equal(CredentialLifecycleResultStatus.Invalid, (await fixture.Service.ExecuteAsync(unexpectedTarget)).Status);
    }

    [Fact]
    public async Task OversizedActiveRunCaptureFailsBeforeRestrictiveMutation()
    {
        using var fixture = await CreatedFixtureAsync("oversized-runs-create");
        fixture.ActiveRuns.Runs = Enumerable.Range(0, 1_025).Select(index => $"run-{index:D4}").ToArray();
        var revision = fixture.Registry.Revision;
        var preview = await fixture.Service.PreviewAsync(fixture.PreviewRequest("oversized-runs-disable", CredentialLifecycleOperationKind.Disable, revision));

        var result = await fixture.Service.ExecuteAsync(fixture.DestructiveRequest("oversized-runs-disable", CredentialLifecycleOperationKind.Disable, revision, preview));

        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, result.Status);
        Assert.Equal(revision, fixture.Registry.Revision);
        Assert.Equal(CredentialLifecycleStatus.Active, Assert.Single((await fixture.Registry.ReadAsync()).Entries).Reference.Status);
    }

    private static async Task<LifecycleFixture> CreatedFixtureAsync(string operationId)
    {
        var fixture = new LifecycleFixture();
        await fixture.Service.ExecuteAsync(fixture.CreateRequest(operationId, 4), destination => Fill(destination, 1));
        return fixture;
    }

    private static CredentialContractId DeriveOperationId(CredentialContractId operationId, string phase)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"credential-lifecycle-phase-v1\n{operationId.Value}\n{phase}"))).ToLowerInvariant();
        return Id("op_" + digest);
    }

    private static int Fill(Span<byte> destination, byte value)
    {
        destination.Fill(value);
        return destination.Length;
    }

    private static string Hash(char value) => "sha256:" + new string(value, 64);
    private static readonly DateTimeOffset _timestamp = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static CredentialContractId Id(string value)
    {
        Assert.True(CredentialContractId.TryParse(value, out var id, out _));
        return id!;
    }

    private sealed class FailingAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) => throw new IOException("Injected audit sink failure.");
        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

}
