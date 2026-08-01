using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Owns capability-catalog authentication and monotonic history outside mutable workspace storage.</summary>
/// <remarks>Implementations are server infrastructure, not secret brokerage or a catalog projection surface.</remarks>
public interface ICapabilityCatalogTrustProvider
{
    /// <summary>Validates that provider-owned trust state is disjoint from the governed workspace before either location is mutated.</summary>
    /// <remarks>Providers without filesystem-backed trust state may return normally. Decorators must preserve their inner provider's validation.</remarks>
    /// <param name="workspaceRootPath">The governed workspace root.</param>
    /// <exception cref="InvalidOperationException">Thrown when provider-owned trust state overlaps the governed workspace.</exception>
    void RequireDisjointWorkspace(string workspaceRootPath);

    /// <summary>Reads the authenticated trust anchor, or <see langword="null"/> when this workspace has never been anchored.</summary>
    Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default);

    /// <summary>Atomically creates the initial trust anchor without replacing any existing state.</summary>
    Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default);

    /// <summary>Creates an authentication tag bound to one workspace, generation, and content digest.</summary>
    Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default);

    /// <summary>Verifies one artifact authentication tag without treating it as current.</summary>
    Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default);

    /// <summary>Atomically advances current to a direct successor while retaining the expected current as previous.</summary>
    Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default);
}
