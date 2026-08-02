using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class CapabilityDependentAdaptersTests
{
    [Fact]
    public async Task Real_loop_adapter_indexes_builtin_and_custom_definitions_in_deterministic_order()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var loops = new LoopDefinitionStore(paths);
        var customLoops = new CustomLoopDefinitionStore(paths);
        await loops.SaveAsync(LoopDefinition.CreateDefaultConversation());
        var custom = CustomLoopDefinition.CreateSeed("custom-loop", "default-assistant", "step-one", "create-custom-loop", DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        Assert.Equal(EmbodySense.Core.Application.Loops.Models.CustomLoopDefinitionStoreStatus.Created, (await customLoops.CreateAsync(custom)).Status);
        Assert.Equal(EmbodySense.Core.Application.Loops.Models.CustomLoopOperationAuditMarkStatus.Marked, await customLoops.MarkOperationOutcomeAuditedAsync(custom.LastMutationOperationId));

        var source = new LoopCapabilityDependentIndexSource(loops, customLoops);
        _ = await source.ReadAsync();
        var captured = await new CapabilityDependentIndex([source]).CaptureAsync();

        Assert.True(captured.Status == CapabilityDependentIndexStatus.Available, captured.Detail);
        Assert.Equal(["custom-loop", BuiltInLoopIds.DefaultConversation], captured.Dependents.Select(dependent => dependent.Identity));
        Assert.All(captured.Dependents, dependent => Assert.Equal(CapabilityAuthorityPosture.AssignedDefinition, dependent.AuthorityPosture));
    }

    [Fact]
    public async Task Skill_and_package_adapters_preserve_domain_identity_revision_and_non_granting_posture()
    {
        var skillManifest = Manifest(CapabilityDependencyManifestKind.Skill, "org.example/skill");
        var packageManifest = Manifest(CapabilityDependencyManifestKind.CapabilityPackage, "org.example/package");
        var skillDiscovery = new StubSkillDependencyManifestDiscovery { Discoveries = [new LocalSkillDependencyDiscovery("skill-one", LocalSkillDependencyDiscoveryStatus.Discovered, skillManifest, skillManifest.Artifact, "ok"), new LocalSkillDependencyDiscovery("no-sidecar", LocalSkillDependencyDiscoveryStatus.NoManifest, null, null, "none")] };
        var packageDiscovery = new StubPackageDependencyManifestDiscovery { Discoveries = [new CapabilityPackageDependencyDiscovery("org.example/package", CapabilityLifecycleTestData.Digest("package").Value, packageManifest)] };

        var captured = await new CapabilityDependentIndex([new SkillCapabilityDependentIndexSource(skillDiscovery), new CapabilityPackageDependentIndexSource(packageDiscovery)]).CaptureAsync();

        Assert.Equal(CapabilityDependentIndexStatus.Available, captured.Status);
        Assert.Equal([CapabilityDependentKind.Skill, CapabilityDependentKind.Package], captured.Dependents.Select(dependent => dependent.Kind));
        Assert.Equal(CapabilityAuthorityPosture.MetadataOnly, captured.Dependents[0].AuthorityPosture);
        Assert.Equal(CapabilityAuthorityPosture.HistoricalEvidence, captured.Dependents[1].AuthorityPosture);
    }

    [Fact]
    public async Task Explicit_role_and_schedule_registration_seams_join_the_same_index_contract()
    {
        var role = CapabilityLifecycleTestData.Dependent("role-one", CapabilityRequirementKind.Required, "*", CapabilityDependentKind.Role) with { AuthorityPosture = CapabilityAuthorityPosture.AssignedDefinition };
        var schedule = CapabilityLifecycleTestData.Dependent("schedule-one", CapabilityRequirementKind.Optional, "*", CapabilityDependentKind.Schedule);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()], new StubRoleCapabilityDependentIndexSource { Dependents = [role] }, new StubScheduleCapabilityDependentIndexSource { Dependents = [schedule] });

        var captured = await index.CaptureAsync();

        Assert.Equal(CapabilityDependentIndexStatus.Available, captured.Status);
        Assert.Equal([CapabilityDependentKind.Role, CapabilityDependentKind.Schedule], captured.Dependents.Select(dependent => dependent.Kind));
    }

    [Fact]
    public async Task Invalid_skill_discovery_fails_the_composite_index_closed()
    {
        var discovery = new StubSkillDependencyManifestDiscovery { Discoveries = [new LocalSkillDependencyDiscovery("forged", LocalSkillDependencyDiscoveryStatus.Invalid, null, null, "invalid")] };

        var captured = await new CapabilityDependentIndex([new SkillCapabilityDependentIndexSource(discovery)]).CaptureAsync();

        Assert.Equal(CapabilityDependentIndexStatus.Unavailable, captured.Status);
        Assert.Empty(captured.Dependents);
    }

    [Fact]
    public async Task Loop_adapter_rejects_malformed_builtin_dependency_evidence()
    {
        using var workspace = new TestWorkspace();
        var invalid = LoopDefinition.CreateDefaultConversation() with { CapabilityRequirements = null! };
        var source = new LoopCapabilityDependentIndexSource(new StubLoopDefinitionStore { Definitions = [invalid] }, new CustomLoopDefinitionStore(new WorkspacePaths(workspace.RootPath)));

        await Assert.ThrowsAsync<FormatException>(() => source.ReadAsync());
    }

    [Fact]
    public async Task Future_domain_registration_seams_reject_cross_domain_forgery()
    {
        var forged = CapabilityLifecycleTestData.Dependent("forged-schedule", CapabilityRequirementKind.Required, "*", CapabilityDependentKind.Schedule);
        var index = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()], new StubRoleCapabilityDependentIndexSource { Dependents = [forged] });

        Assert.Equal(CapabilityDependentIndexStatus.Unavailable, (await index.CaptureAsync()).Status);
    }

    [Fact]
    public async Task Package_adapter_rejects_manifest_subject_substitution()
    {
        var manifest = Manifest(CapabilityDependencyManifestKind.CapabilityPackage, "org.example/other-package");
        var discovery = new StubPackageDependencyManifestDiscovery { Discoveries = [new CapabilityPackageDependencyDiscovery("org.example/package", CapabilityLifecycleTestData.Digest("package").Value, manifest)] };

        var captured = await new CapabilityDependentIndex([new CapabilityPackageDependentIndexSource(discovery)]).CaptureAsync();

        Assert.Equal(CapabilityDependentIndexStatus.Unavailable, captured.Status);
    }

    [Fact]
    public async Task Composite_index_rejects_null_dependents_and_unproved_package_revisions()
    {
        var invalidSource = new StubCapabilityDependentIndexSource { Dependents = [null!] };
        Assert.Equal(CapabilityDependentIndexStatus.Unavailable, (await new CapabilityDependentIndex([invalidSource]).CaptureAsync()).Status);

        var manifest = Manifest(CapabilityDependencyManifestKind.CapabilityPackage, "org.example/package");
        var discovery = new StubPackageDependencyManifestDiscovery { Discoveries = [new CapabilityPackageDependencyDiscovery("org.example/package", "not-a-digest", manifest)] };
        Assert.Equal(CapabilityDependentIndexStatus.Unavailable, (await new CapabilityDependentIndex([new CapabilityPackageDependentIndexSource(discovery)]).CaptureAsync()).Status);
    }

    private static CapabilityDependencyManifest Manifest(CapabilityDependencyManifestKind kind, string subject)
    {
        Assert.True(CapabilityId.TryParse(subject, out var subjectId, out _));
        return new CapabilityDependencyManifest(1, kind, subjectId!, [], [], new CapabilityDependencyArtifactMetadata(null, null));
    }
}
