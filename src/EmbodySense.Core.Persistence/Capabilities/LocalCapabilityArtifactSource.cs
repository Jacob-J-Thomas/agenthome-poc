using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Reads bounded regular files from one configured no-link local source root.</summary>
public sealed class LocalCapabilityArtifactSource : ILocalCapabilityArtifactSource
{
    private readonly string _sourceRoot;
    private readonly StringComparison _comparison;

    /// <summary>Creates a local source confined to one existing root.</summary>
    public LocalCapabilityArtifactSource(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        _sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <inheritdoc />
    public async Task<CapabilityArtifactContent> ReadAsync(CapabilityArtifactSourceReference source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != CapabilityArtifactSourceKind.Local || !Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri) || !uri.IsFile || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.Equals(uri.AbsoluteUri, source.Uri, StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical local file source is required.", nameof(source));
        }

        var path = Path.GetFullPath(uri.LocalPath);
        RequireContained(path);
        await using var session = CapabilityCatalogPathSession.Open(_sourceRoot, _comparison, createRoot: false) ?? throw new DirectoryNotFoundException("The configured local artifact source root is unavailable.");
        var bytes = await session.ReadAllBytesAsync(path, CapabilityArtifactManifestValidator.MaximumArtifactBytes, cancellationToken);
        if (bytes.Length == 0)
        {
            throw new IOException("The local artifact is empty.");
        }
        return new CapabilityArtifactContent(bytes);
    }

    private void RequireContained(string path)
    {
        var prefix = _sourceRoot + Path.DirectorySeparatorChar;
        if (string.Equals(path, _sourceRoot, _comparison) || !path.StartsWith(prefix, _comparison))
        {
            throw new UnauthorizedAccessException("The local artifact source escapes its configured root.");
        }
    }

}
