using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Discovers standard <c>SKILL.md</c> entries and closed dependency sidecars under one configured skills root.</summary>
/// <remarks>Discovery is read-only and never declares, installs, trusts, enables, assigns, or executes a discovered skill.</remarks>
public sealed class LocalSkillDependencyManifestDiscovery : ISkillDependencyManifestDiscovery
{
    private const int MaximumSkillDirectories = 256;
    private const int MaximumSkillMarkdownBytes = 131_072;
    private readonly WorkspacePaths _paths;
    private readonly StringComparison _pathComparison;
    private readonly ILocalSkillDependencyManifestDiscoveryBarrier? _barrier;

    /// <summary>Creates discovery rooted at the configured workspace skills path.</summary>
    public LocalSkillDependencyManifestDiscovery(WorkspacePaths paths) : this(paths, null)
    {
    }

    /// <summary>Creates discovery rooted at the configured workspace skills path with an optional bounded read-coordination barrier.</summary>
    public LocalSkillDependencyManifestDiscovery(WorkspacePaths paths, ILocalSkillDependencyManifestDiscoveryBarrier? barrier)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _barrier = barrier;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalSkillDependencyDiscovery>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.SkillsPath));
        CapabilityCatalogPathSession? session;
        try
        {
            session = CapabilityCatalogPathSession.Open(root, _pathComparison, createRoot: false);
        }
        catch (IOException)
        {
            return [new LocalSkillDependencyDiscovery(string.Empty, LocalSkillDependencyDiscoveryStatus.UnsafePath, null, null, "The configured local skills scope is a reparse point or cannot be physically bound.")];
        }

        if (session is null)
        {
            return [];
        }

        using (session)
        {
            return await DiscoverBoundAsync(session, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<LocalSkillDependencyDiscovery>> DiscoverBoundAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> directories;
        try
        {
            if (!session.TryEnumerateDirectories(session.Root, MaximumSkillDirectories, out directories))
            {
                return [new LocalSkillDependencyDiscovery(string.Empty, LocalSkillDependencyDiscoveryStatus.LimitExceeded, null, null, "The local skill root entry count exceeds the schema-1 bound.")];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [new LocalSkillDependencyDiscovery(string.Empty, LocalSkillDependencyDiscoveryStatus.UnsafePath, null, null, "The configured local skills scope could not be enumerated through its retained physical binding.")];
        }

        var results = new List<LocalSkillDependencyDiscovery>();
        foreach (var directoryName in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(session.Root, directoryName);
            var name = directoryName;
            try
            {
                if (!session.DirectoryExists(directory))
                {
                    results.Add(new LocalSkillDependencyDiscovery(name, LocalSkillDependencyDiscoveryStatus.UnsafePath, null, null, "The skill directory disappeared or could not be physically bound beneath the configured skills scope."));
                    continue;
                }

                var skillPath = Path.Combine(directory, "SKILL.md");
                var manifestPath = Path.Combine(directory, "capability-dependencies.json");
                _barrier?.BeforeSkillRead(directory);
                var skillBytes = await session.TryReadAllBytesAllowEmptyBoundAsync(skillPath, MaximumSkillMarkdownBytes, cancellationToken);
                var manifestBytes = await session.TryReadAllBytesBoundAsync(manifestPath, CapabilityContractLimits.MaxDependencyManifestJsonCharacters, cancellationToken);
                if (skillBytes is null || manifestBytes is null)
                {
                    results.Add(new LocalSkillDependencyDiscovery(name, LocalSkillDependencyDiscoveryStatus.NoManifest, null, null, "A standard SKILL.md entrypoint and dependency sidecar are both required for discovery."));
                    continue;
                }

                var manifestText = new UTF8Encoding(false, true).GetString(manifestBytes);
                if (manifestText.StartsWith('\uFEFF'))
                {
                    manifestText = manifestText[1..];
                }
                if (!CapabilityDependencyManifestJson.TryDeserialize(manifestText, out var manifest, out _))
                {
                    results.Add(new LocalSkillDependencyDiscovery(name, LocalSkillDependencyDiscoveryStatus.Invalid, null, null, "The dependency sidecar is malformed, unsupported, or carries prohibited metadata."));
                    continue;
                }

                var checksum = CapabilityIntegrityDigest.Compute(skillBytes);
                if (manifest!.Kind != CapabilityDependencyManifestKind.Skill || manifest.Artifact.Checksum is not null && !manifest.Artifact.Checksum.FixedTimeEquals(checksum) || !DirectoryMatchesSubject(name, manifest.SubjectId))
                {
                    results.Add(new LocalSkillDependencyDiscovery(name, LocalSkillDependencyDiscoveryStatus.Invalid, null, null, "The skill manifest kind, subject identity, or declared checksum does not match the discovered skill."));
                    continue;
                }

                results.Add(new LocalSkillDependencyDiscovery(name, LocalSkillDependencyDiscoveryStatus.Discovered, manifest, new CapabilityDependencyArtifactMetadata(checksum, manifest.Artifact.Signature), "The bounded local skill dependency manifest was discovered."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                results.Add(new LocalSkillDependencyDiscovery(name, LocalSkillDependencyDiscoveryStatus.UnsafePath, null, null, "The local skill could not be physically bound beneath the configured skills scope."));
            }
            catch (Exception exception) when (exception is FormatException or UnauthorizedAccessException or DecoderFallbackException)
            {
                results.Add(new LocalSkillDependencyDiscovery(name, LocalSkillDependencyDiscoveryStatus.Invalid, null, null, "The local skill could not be read safely."));
            }
        }

        return results;
    }

    private static bool DirectoryMatchesSubject(string directoryName, CapabilityId subjectId) => string.Equals(directoryName, subjectId.Value[(subjectId.Value.LastIndexOf('/') + 1)..], StringComparison.Ordinal);
}
