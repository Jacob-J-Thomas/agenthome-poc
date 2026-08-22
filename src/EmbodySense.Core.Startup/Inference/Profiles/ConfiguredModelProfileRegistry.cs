using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Projects one server-owned host configuration into safe exact model-profile metadata and adapter posture.</summary>
public sealed class ConfiguredModelProfileRegistry : IModelProfileMetadataSource, IModelProfileDefaultSource, IModelProfileAdapterRegistry
{
    private const long MaximumExecutableBytes = 512L * 1024 * 1024;
    private const int ConservativeUnprovenTokenLimit = 1;
    private readonly string _configuredExecutablePath;
    private readonly string _executableContentHash;
    private readonly string _executablePath;
    private readonly GovernedModelProfileMetadata _metadata;
    private readonly CapabilityId _profileId;
    private readonly string _sourceRevisionHash;
    private readonly string _registryRevisionHash;

    /// <summary>Creates one exact safe profile projection from trusted host options.</summary>
    public ConfiguredModelProfileRegistry(
        LlmInferenceClientOptions options,
        CodexRuntimeStatus runtimeStatus)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimeStatus);
        var descriptor = BuiltInCapabilityCatalog.Descriptors.Single(value =>
            string.Equals(value.Id.Value, BuiltInCapabilityCatalog.CodexModelProfileCapabilityId, StringComparison.Ordinal));
        if (!CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _)
            || options.Surface != LlmInferenceSurface.OpenAiCodex
            || runtimeStatus.Compatibility != CodexRuntimeCompatibility.Compatible
            || string.IsNullOrWhiteSpace(runtimeStatus.ResolvedExecutablePath)
            || string.IsNullOrWhiteSpace(runtimeStatus.Version)
            || runtimeStatus.Version.Length > 256
            || runtimeStatus.Version.Any(character => char.IsControl(character) && character is not '\t')
            || !string.Equals(runtimeStatus.ConfiguredModel, options.Model, StringComparison.Ordinal)
            || !PathsEqual(runtimeStatus.ResolvedExecutablePath, options.CodexExecutablePath))
        {
            throw new ArgumentException("The selected provider requires one exact compatible and version-probed runtime.", nameof(runtimeStatus));
        }

        _profileId = descriptor.Id;
        _configuredExecutablePath = Path.GetFullPath(runtimeStatus.ResolvedExecutablePath);
        _executablePath = ConfiguredModelExecutableSnapshotLease.ResolveExactExecutablePath(_configuredExecutablePath);
        ConfiguredModelExecutableSnapshotLease.ScavengeOrphanedSnapshots();
        _executableContentHash = ConfiguredModelExecutableSnapshotLease.ReadSourceContentHash(_executablePath, MaximumExecutableBytes);
        var publicModelId = string.IsNullOrWhiteSpace(options.Model) ? "configured-default" : RequirePublicModelId(options.Model);
        var runtimeEvidenceHash = CustomLoopTraceContentHash.Compute(string.Join('\n',
            "embodysense.configured-model-runtime-evidence.v1",
            runtimeStatus.Compatibility.ToString(),
            runtimeStatus.Version,
            publicModelId,
            _executableContentHash));
        var configurationHash = CustomLoopTraceContentHash.Compute(string.Join('\n',
            "embodysense.configured-model-profile.v1",
            options.Surface.ToString(),
            publicModelId,
            options.CodexSandbox,
            runtimeEvidenceHash));
        _metadata = GovernedModelProfileMetadata.Create(
            1,
            identity!,
            "openai",
            "codex-app-server",
            publicModelId,
            "v1",
            1,
            configurationHash,
            "Configured remote Codex app-server model profile whose finite output-token ceiling cannot be hard-enforced by the installed adapter.",
            [GovernedModelModality.Text],
            [GovernedModelCapability.ToolCalling, GovernedModelCapability.Streaming],
            ConservativeUnprovenTokenLimit,
            ConservativeUnprovenTokenLimit,
            GovernedModelPrivacyPosture.Create(
                1,
                GovernedModelLocality.Remote,
                EmbodySense.Core.Common.Capabilities.Models.CapabilityEgressMode.Unrestricted,
                [],
                [SensitiveDataClass()],
                [],
                GovernedModelRetentionPosture.Indefinite,
                GovernedModelTrainingPosture.Allowed),
            GovernedModelUsageSupportPolicy.Create(
                GovernedModelUsageSupport.AuthoritativeAfterDispatch,
                GovernedModelUsageSupport.AuthoritativeAfterDispatch,
                GovernedModelUsageSupport.AuthoritativeAfterDispatch,
                GovernedModelUsageSupport.AuthoritativeAfterDispatch,
                GovernedModelUsageSupport.Unavailable),
            [],
            ["provider-inference"]);
        _sourceRevisionHash = CustomLoopTraceContentHash.Compute("embodysense.configured-model-profile-source.v1\n" + _metadata.ContentHash);
        _registryRevisionHash = CustomLoopTraceContentHash.Compute("embodysense.configured-model-adapter-registry.v2\n" + _metadata.ContentHash);
    }

    /// <inheritdoc />
    public Task<ModelProfileSourceReadResult> ReadAsync(CapabilityId profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(profileId.Equals(_profileId) && CurrentRuntimeMatches()
            ? new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.Found, _metadata, _sourceRevisionHash)
            : new ModelProfileSourceReadResult(
                profileId.Equals(_profileId) ? ModelProfileSourceReadStatus.Unavailable : ModelProfileSourceReadStatus.NotFound,
                null,
                null));
    }

    /// <inheritdoc />
    public Task<ModelProfileDefaultReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CurrentRuntimeMatches()
            ? new ModelProfileDefaultReadResult(ModelProfileDefaultReadStatus.Found, _profileId, _sourceRevisionHash)
            : new ModelProfileDefaultReadResult(ModelProfileDefaultReadStatus.Unavailable, null, null));
    }

    /// <inheritdoc />
    public Task<ModelProfileAdapterPosture> ReadPostureAsync(GovernedModelProfileMetadata metadata, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownsProfile = metadata is not null
            && string.Equals(metadata.ContentHash, _metadata.ContentHash, StringComparison.Ordinal);
        var ready = ownsProfile && CurrentRuntimeMatches() && !HasUnforwardableOutputTokenCeiling();
        return Task.FromResult(new ModelProfileAdapterPosture(
            ready
                ? ModelProfileAdapterPostureStatus.Ready
                : ownsProfile ? ModelProfileAdapterPostureStatus.Unavailable : ModelProfileAdapterPostureStatus.Unregistered,
            metadata?.ContentHash ?? _metadata.ContentHash,
            _registryRevisionHash));
    }

    private bool CurrentRuntimeMatches()
    {
        try
        {
            var currentExactPath = ConfiguredModelExecutableSnapshotLease.ResolveExactExecutablePath(_configuredExecutablePath);
            return PathsEqual(currentExactPath, _executablePath)
                && string.Equals(
                    ConfiguredModelExecutableSnapshotLease.ReadSourceContentHash(currentExactPath, MaximumExecutableBytes),
                    _executableContentHash,
                    StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private bool HasUnforwardableOutputTokenCeiling() => _metadata.MaximumOutputTokens > 0;

    internal Task<ConfiguredModelExecutableSnapshotLease> AcquireExecutableSnapshotAsync(CancellationToken cancellationToken)
        => ConfiguredModelExecutableSnapshotLease.AcquireAsync(
            _executablePath,
            _executableContentHash,
            MaximumExecutableBytes,
            cancellationToken);

    private static bool PathsEqual(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch
        {
            return false;
        }
    }

    private static CapabilityDataClass SensitiveDataClass()
    {
        _ = CapabilityDataClass.TryParse("sensitive", out var value, out _);
        return value!;
    }

    private static string RequirePublicModelId(string value)
    {
        if (value.Length > 128
            || value[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9')
            || value.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '/')))
        {
            throw new ArgumentException("The configured model identity is not a bounded public model token.", nameof(value));
        }

        return value;
    }
}
