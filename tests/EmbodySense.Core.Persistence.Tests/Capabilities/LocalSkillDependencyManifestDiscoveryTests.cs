using System.Text;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

public sealed class LocalSkillDependencyManifestDiscoveryTests
{
    [Fact]
    public async Task Discovery_reads_standard_skill_sidecars_deterministically_and_survives_restart()
    {
        using var workspace = new TestWorkspace();
        await WriteSkillAsync(workspace, "a", "org.example/a", "# a\n");
        await WriteSkillAsync(workspace, "z", "org.example/z", "# z\n");

        var first = await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync();
        var second = await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync();

        Assert.Equal(["a", "z"], first.Select(item => item.DirectoryName));
        Assert.All(first, item => Assert.Equal(LocalSkillDependencyDiscoveryStatus.Discovered, item.Status));
        Assert.Equal(first.Select(item => item.Artifact!.Checksum!.Value), second.Select(item => item.Artifact!.Checksum!.Value));
        Assert.All(first, item => Assert.Equal(CapabilityDependencyManifestKind.Skill, item.Manifest!.Kind));
    }

    [Fact]
    public async Task Discovery_rejects_checksum_tampering_and_ignores_files_outside_the_configured_scope()
    {
        using var workspace = new TestWorkspace();
        await WriteSkillAsync(workspace, "tampered", "org.example/tampered", "# original\n");
        await File.WriteAllTextAsync(workspace.File(".agent", "skills", "tampered", "SKILL.md"), "# changed\n");
        var outside = Path.Combine(workspace.RootPath, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "capability-dependencies.json"), "{}", Encoding.UTF8);

        var results = await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync();

        var result = Assert.Single(results);
        Assert.Equal("tampered", result.DirectoryName);
        Assert.Equal(LocalSkillDependencyDiscoveryStatus.Invalid, result.Status);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task Discovery_fails_closed_for_hostile_authority_metadata_and_oversized_skill_content()
    {
        using var workspace = new TestWorkspace();
        var directory = workspace.File(".agent", "skills", "unsafe");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), new string('x', 131_073), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(directory, "capability-dependencies.json"), "{\"schemaVersion\":1,\"kind\":\"skill\",\"subjectId\":\"org.example/unsafe\",\"required\":[],\"optional\":[],\"artifact\":{\"checksum\":null,\"signature\":null},\"permissions\":[\"all\"]}", Encoding.UTF8);

        var result = Assert.Single(await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync());

        Assert.Equal(LocalSkillDependencyDiscoveryStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Discovery_returns_empty_when_the_configured_skills_root_does_not_exist()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var results = await new LocalSkillDependencyManifestDiscovery(paths).DiscoverAsync();

        Assert.Empty(results);
        Assert.False(Directory.Exists(paths.SkillsPath));
    }

    [Fact]
    public async Task Discovery_reports_no_manifest_when_either_required_sidecar_is_missing()
    {
        using var workspace = new TestWorkspace();
        var skillsPath = new WorkspacePaths(workspace.RootPath).SkillsPath;
        var skillOnly = Path.Combine(skillsPath, "skill-only");
        var manifestOnly = Path.Combine(skillsPath, "manifest-only");
        Directory.CreateDirectory(skillOnly);
        Directory.CreateDirectory(manifestOnly);
        await File.WriteAllTextAsync(Path.Combine(skillOnly, "SKILL.md"), "# skill only\n", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(manifestOnly, "capability-dependencies.json"), "{}", Encoding.UTF8);

        var results = await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync();

        Assert.Equal(["manifest-only", "skill-only"], results.Select(result => result.DirectoryName));
        Assert.All(results, result => Assert.Equal(LocalSkillDependencyDiscoveryStatus.NoManifest, result.Status));
    }

    [Fact]
    public async Task Discovery_accepts_a_bom_prefixed_canonical_dependency_sidecar()
    {
        using var workspace = new TestWorkspace();
        await WriteSkillAsync(workspace, "bom", "org.example/bom", "# bom\n");
        var manifestPath = workspace.File(".agent", "skills", "bom", "capability-dependencies.json");
        var canonical = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, "\uFEFF" + canonical, new UTF8Encoding(false));

        var result = Assert.Single(await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync());

        Assert.Equal(LocalSkillDependencyDiscoveryStatus.Discovered, result.Status);
        Assert.Equal("org.example/bom", result.Manifest!.SubjectId.Value);
    }

    [Fact]
    public async Task Discovery_rejects_malformed_dependency_json()
    {
        using var workspace = new TestWorkspace();
        var directory = workspace.File(".agent", "skills", "malformed");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "# malformed\n", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(directory, "capability-dependencies.json"), "{", Encoding.UTF8);

        var result = Assert.Single(await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync());

        Assert.Equal(LocalSkillDependencyDiscoveryStatus.Invalid, result.Status);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task Discovery_honors_cancellation_before_reading_a_bound_skill()
    {
        using var workspace = new TestWorkspace();
        var directory = workspace.File(".agent", "skills", "cancelled");
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync(cancellation.Token));
    }

    [Fact]
    public async Task Discovery_rejects_reparse_configured_skills_roots_and_ancestors_when_supported()
    {
        using var skillsWorkspace = new TestWorkspace();
        using var skillsTarget = new TestWorkspace();
        var skillsPaths = new WorkspacePaths(skillsWorkspace.RootPath);
        await WriteSkillAsync(skillsTarget, "linked", "org.example/linked", "# linked\n");
        Directory.CreateDirectory(skillsPaths.AgentPath);
        if (!TryCreateDirectoryLink(skillsPaths.SkillsPath, Path.Combine(skillsTarget.RootPath, ".agent", "skills")))
        {
            return;
        }

        var linkedSkills = Assert.Single(await new LocalSkillDependencyManifestDiscovery(skillsPaths).DiscoverAsync());

        Assert.Equal(LocalSkillDependencyDiscoveryStatus.UnsafePath, linkedSkills.Status);
        Assert.Null(linkedSkills.Manifest);

        using var ancestorWorkspace = new TestWorkspace();
        using var ancestorTarget = new TestWorkspace();
        var ancestorPaths = new WorkspacePaths(ancestorWorkspace.RootPath);
        await WriteSkillAsync(ancestorTarget, "ancestor", "org.example/ancestor", "# ancestor\n");
        if (!TryCreateDirectoryLink(ancestorPaths.AgentPath, Path.Combine(ancestorTarget.RootPath, ".agent")))
        {
            return;
        }

        var linkedAncestor = Assert.Single(await new LocalSkillDependencyManifestDiscovery(ancestorPaths).DiscoverAsync());

        Assert.Equal(LocalSkillDependencyDiscoveryStatus.UnsafePath, linkedAncestor.Status);
        Assert.Null(linkedAncestor.Manifest);
    }

    [Fact]
    public async Task Discovery_rejects_a_linked_entry_within_the_configured_skills_root_when_supported()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteSkillAsync(workspace, "valid", "org.example/valid", "# valid\n");
        var target = workspace.File("outside-entry");
        var linkedEntry = Path.Combine(paths.SkillsPath, "linked-entry");
        await File.WriteAllTextAsync(target, "outside", Encoding.UTF8);
        try
        {
            File.CreateSymbolicLink(linkedEntry, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = Assert.Single(await new LocalSkillDependencyManifestDiscovery(paths).DiscoverAsync());

        Assert.Equal(string.Empty, result.DirectoryName);
        Assert.Equal(LocalSkillDependencyDiscoveryStatus.UnsafePath, result.Status);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task Discovery_fails_closed_when_a_bound_skill_directory_is_replaced_before_its_single_reads()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteSkillAsync(workspace, "race", "org.example/race", "# original\n");
        var barrier = new SubstitutingDiscoveryBarrier(paths.SkillsPath, "race");

        var result = Assert.Single(await new LocalSkillDependencyManifestDiscovery(paths, barrier).DiscoverAsync());

        if (barrier.Substituted)
        {
            Assert.Equal(LocalSkillDependencyDiscoveryStatus.UnsafePath, result.Status);
            Assert.Null(result.Manifest);
        }
        else if (barrier.MovedWithoutReplacement)
        {
            Assert.Equal(LocalSkillDependencyDiscoveryStatus.NoManifest, result.Status);
            Assert.Null(result.Manifest);
        }
        else
        {
            Assert.True(barrier.SubstitutionBlocked);
            Assert.Equal(LocalSkillDependencyDiscoveryStatus.Discovered, result.Status);
        }
    }

    [Theory]
    [InlineData(257)]
    [InlineData(640)]
    public async Task Discovery_reports_limit_exceeded_for_excess_skill_directories(int entryCount)
    {
        using var workspace = new TestWorkspace();
        var skillsPath = new WorkspacePaths(workspace.RootPath).SkillsPath;
        Directory.CreateDirectory(skillsPath);
        for (var index = 0; index < entryCount; index++)
        {
            Directory.CreateDirectory(Path.Combine(skillsPath, $"skill-{index:D4}"));
        }

        var result = Assert.Single(await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync());

        Assert.Equal(LocalSkillDependencyDiscoveryStatus.LimitExceeded, result.Status);
        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData(257)]
    [InlineData(640)]
    public async Task Discovery_reports_limit_exceeded_for_excess_regular_files(int entryCount)
    {
        using var workspace = new TestWorkspace();
        var skillsPath = new WorkspacePaths(workspace.RootPath).SkillsPath;
        Directory.CreateDirectory(skillsPath);
        for (var index = 0; index < entryCount; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(skillsPath, $"clutter-{index:D4}.txt"), string.Empty);
        }

        var result = Assert.Single(await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync());

        Assert.Equal(LocalSkillDependencyDiscoveryStatus.LimitExceeded, result.Status);
        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData(129, 128)]
    [InlineData(320, 320)]
    public async Task Discovery_reports_limit_exceeded_for_excess_mixed_entries(int directoryCount, int fileCount)
    {
        using var workspace = new TestWorkspace();
        var skillsPath = new WorkspacePaths(workspace.RootPath).SkillsPath;
        Directory.CreateDirectory(skillsPath);
        for (var index = 0; index < directoryCount; index++)
        {
            Directory.CreateDirectory(Path.Combine(skillsPath, $"skill-{index:D4}"));
        }
        for (var index = 0; index < fileCount; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(skillsPath, $"clutter-{index:D4}.txt"), string.Empty);
        }

        var result = Assert.Single(await new LocalSkillDependencyManifestDiscovery(new WorkspacePaths(workspace.RootPath)).DiscoverAsync());

        Assert.Equal(LocalSkillDependencyDiscoveryStatus.LimitExceeded, result.Status);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task Discovery_ignores_bounded_regular_file_clutter()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteSkillAsync(workspace, "valid", "org.example/valid", "# valid\n");
        for (var index = 0; index < 8; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(paths.SkillsPath, $"clutter-{index:D2}.txt"), string.Empty);
        }

        var result = Assert.Single(await new LocalSkillDependencyManifestDiscovery(paths).DiscoverAsync());

        Assert.Equal(LocalSkillDependencyDiscoveryStatus.Discovered, result.Status);
        Assert.Equal("valid", result.DirectoryName);
    }

    private static async Task WriteSkillAsync(TestWorkspace workspace, string directoryName, string subjectId, string content)
    {
        var directory = workspace.File(".agent", "skills", directoryName);
        Directory.CreateDirectory(directory);
        var checksum = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content));
        Assert.True(CapabilityId.TryParse(subjectId, out var subject, out _));
        var manifest = new CapabilityDependencyManifest(1, CapabilityDependencyManifestKind.Skill, subject!, [], [], new CapabilityDependencyArtifactMetadata(checksum, "test-signature"));
        Assert.True(CapabilityDependencyManifestJson.TrySerialize(manifest, out var json, out _));
        var utf8 = new UTF8Encoding(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), content, utf8);
        await File.WriteAllTextAsync(Path.Combine(directory, "capability-dependencies.json"), json!, utf8);
    }

    private static bool TryCreateDirectoryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed class SubstitutingDiscoveryBarrier : ILocalSkillDependencyManifestDiscoveryBarrier
    {
        private readonly string _skillsPath;
        private readonly string _directoryName;

        public SubstitutingDiscoveryBarrier(string skillsPath, string directoryName)
        {
            _skillsPath = skillsPath;
            _directoryName = directoryName;
        }

        public bool Substituted { get; private set; }

        public bool SubstitutionBlocked { get; private set; }

        public bool MovedWithoutReplacement { get; private set; }

        public void BeforeSkillRead(string directoryPath)
        {
            if (!string.Equals(directoryPath, Path.Combine(_skillsPath, _directoryName), StringComparison.Ordinal))
            {
                return;
            }

            var displaced = Path.Combine(_skillsPath, _directoryName + "-displaced");
            try
            {
                Directory.Move(directoryPath, displaced);
                MovedWithoutReplacement = true;
                Directory.CreateDirectory(directoryPath);
                Substituted = true;
                MovedWithoutReplacement = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                SubstitutionBlocked = true;
            }
        }
    }
}
