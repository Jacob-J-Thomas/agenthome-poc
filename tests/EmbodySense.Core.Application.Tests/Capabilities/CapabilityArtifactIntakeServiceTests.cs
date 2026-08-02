using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Capabilities;

public sealed class CapabilityArtifactIntakeServiceTests
{
    [Fact]
    public async Task Verified_local_artifact_is_staged_then_activated()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.IntakeAsync(new CapabilityArtifactIntakeRequest(CapabilityArtifactTestData.Manifest(), 0, "intake-1"));

        Assert.Equal(CapabilityArtifactIntakeStatus.Activated, result.Status);
        Assert.Equal(1, fixture.Local.Calls);
        Assert.Equal(0, fixture.Remote.Calls);
        Assert.Equal(1, fixture.Trust.Calls);
        Assert.Equal(1, fixture.Store.StageCalls);
        Assert.Equal(1, fixture.Store.ActivationCalls);
        Assert.Equal(3, fixture.Audit.Events.Count);
        Assert.Contains(fixture.Audit.Events, item => item.Action == "capability.artifact.verification");
        Assert.Contains(fixture.Audit.Events, item => item.Action == "capability.artifact.activation");
        var intakeAudit = Assert.Single(fixture.Audit.Events, item => item.Action == "capability.artifact.intake");
        Assert.DoesNotContain("sourceUri", intakeAudit.Metadata.Keys);
    }

    [Fact]
    public async Task Remote_manifest_uses_only_remote_source()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.IntakeAsync(new CapabilityArtifactIntakeRequest(CapabilityArtifactTestData.Manifest(CapabilityArtifactSourceKind.Remote), 0, "remote-1"));

        Assert.Equal(CapabilityArtifactIntakeStatus.Activated, result.Status);
        Assert.Equal(0, fixture.Local.Calls);
        Assert.Equal(1, fixture.Remote.Calls);
    }

    [Fact]
    public async Task Checksum_mismatch_blocks_trust_staging_and_activation()
    {
        var fixture = new Fixture();
        fixture.Local.Handler = (_, _) => Task.FromResult(new CapabilityArtifactContent("tampered"u8));

        var result = await fixture.Service.IntakeAsync(new CapabilityArtifactIntakeRequest(CapabilityArtifactTestData.Manifest(), 0, "tampered-1"));

        Assert.Equal(CapabilityArtifactIntakeStatus.IntegrityRejected, result.Status);
        Assert.Equal(1, fixture.Trust.Calls);
        Assert.Equal(0, fixture.Store.StageCalls);
    }

    [Theory]
    [InlineData(CapabilityArtifactTrustStatus.Rejected, CapabilityArtifactIntakeStatus.TrustRejected)]
    [InlineData(CapabilityArtifactTrustStatus.Unavailable, CapabilityArtifactIntakeStatus.Unavailable)]
    public async Task Server_owned_trust_failure_blocks_staging(CapabilityArtifactTrustStatus trustStatus, CapabilityArtifactIntakeStatus expected)
    {
        var fixture = new Fixture();
        fixture.Trust.Decision = new CapabilityArtifactTrustDecision(trustStatus, "test", "Trust failed.");

        var result = await fixture.Service.IntakeAsync(new CapabilityArtifactIntakeRequest(CapabilityArtifactTestData.Manifest(), 0, "trust-1"));

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, fixture.Local.Calls);
        Assert.Equal(0, fixture.Store.StageCalls);
    }

    [Fact]
    public async Task Platform_and_host_version_mismatch_block_source_access()
    {
        var fixture = new Fixture(platform: CapabilityArtifactTestData.Platform("linux/x64"));

        var result = await fixture.Service.IntakeAsync(new CapabilityArtifactIntakeRequest(CapabilityArtifactTestData.Manifest(), 0, "platform-1"));

        Assert.Equal(CapabilityArtifactIntakeStatus.Incompatible, result.Status);
        Assert.Equal(0, fixture.Local.Calls);
    }

    [Fact]
    public async Task Unenforceable_or_secret_bearing_artifact_remains_unavailable()
    {
        var fixture = new Fixture();
        fixture.Host.Availability = new CapabilityExecutableAvailability(CapabilityExecutableAvailabilityStatus.Unavailable, "Isolation unavailable.");

        var result = await fixture.Service.IntakeAsync(new CapabilityArtifactIntakeRequest(CapabilityArtifactTestData.Manifest(secrets: true), 0, "secret-1"));

        Assert.Equal(CapabilityArtifactIntakeStatus.RequirementsUnavailable, result.Status);
        Assert.Equal(0, fixture.Local.Calls);
    }

    [Theory]
    [InlineData(CapabilityArtifactStoreStatus.Replayed, CapabilityArtifactIntakeStatus.Replayed)]
    [InlineData(CapabilityArtifactStoreStatus.Conflict, CapabilityArtifactIntakeStatus.Conflict)]
    [InlineData(CapabilityArtifactStoreStatus.Invalid, CapabilityArtifactIntakeStatus.Invalid)]
    public async Task Activation_store_outcomes_remain_structured(CapabilityArtifactStoreStatus storeStatus, CapabilityArtifactIntakeStatus expected)
    {
        var fixture = new Fixture();
        fixture.Store.ActivationStatus = storeStatus;

        var result = await fixture.Service.IntakeAsync(new CapabilityArtifactIntakeRequest(CapabilityArtifactTestData.Manifest(), 0, "store-1"));

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Hostile_source_failure_is_projected_without_exception_or_secret()
    {
        var fixture = new Fixture();
        fixture.Local.Handler = (_, _) => throw new IOException("password=hunter2 C:\\private\\token.txt");

        var result = await fixture.Service.IntakeAsync(new CapabilityArtifactIntakeRequest(CapabilityArtifactTestData.Manifest(), 0, "source-failure"));

        Assert.Equal(CapabilityArtifactIntakeStatus.Unavailable, result.Status);
        Assert.DoesNotContain("hunter2", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("private", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_staging()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        fixture.Local.Handler = (_, token) => Task.FromCanceled<CapabilityArtifactContent>(token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.IntakeAsync(new CapabilityArtifactIntakeRequest(CapabilityArtifactTestData.Manifest(), 0, "cancel-1"), cancellation.Token));
        Assert.Equal(0, fixture.Store.StageCalls);
    }

    private sealed class Fixture
    {
        internal Fixture(EmbodySense.Core.Common.Capabilities.CapabilityPlatform? platform = null)
        {
            Service = new CapabilityArtifactIntakeService(Local, Remote, Trust, Store, Host, platform ?? CapabilityArtifactTestData.Platform(), CapabilityArtifactTestData.Version(), Audit);
        }

        internal StubLocalCapabilityArtifactSource Local { get; } = new();
        internal StubRemoteCapabilityArtifactSource Remote { get; } = new();
        internal StubCapabilityArtifactTrustVerifier Trust { get; } = new();
        internal StubCapabilityArtifactStore Store { get; } = new();
        internal StubCapabilityExecutableHost Host { get; } = new();
        internal RecordingCapabilityAuditLog Audit { get; } = new();
        internal CapabilityArtifactIntakeService Service { get; }
    }
}
