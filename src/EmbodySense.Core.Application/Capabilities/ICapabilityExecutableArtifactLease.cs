using Microsoft.Win32.SafeHandles;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Retains the exact proved executable artifact identity for one bounded invocation.</summary>
/// <remarks>The lease owns neither caller authority nor ambient path trust. Platform isolation adapters must start the executable represented by <see cref="ExecutableHandle"/> and must fail closed when they cannot bind process creation to that handle.</remarks>
public interface ICapabilityExecutableArtifactLease : IAsyncDisposable
{
    /// <summary>Gets the server-resolved immutable artifact root used as the working scope.</summary>
    string ArtifactRoot { get; }

    /// <summary>Gets the server-resolved executable path corresponding to the retained handle.</summary>
    string ExecutablePath { get; }

    /// <summary>Gets the retained executable handle without transferring ownership.</summary>
    SafeFileHandle ExecutableHandle { get; }

    /// <summary>Gets the exact proved artifact digest bound to this lease.</summary>
    CapabilityIntegrityDigest ArtifactDigest { get; }

    /// <summary>Gets the exact proved activation revision bound to this lease.</summary>
    long ActivationRevision { get; }

    /// <summary>Revalidates current lifecycle authority and retains its transaction fence through process launch.</summary>
    /// <param name="cancellationToken">The cancellation token used while acquiring and validating launch authority.</param>
    /// <returns>A launch fence when the exact artifact remains enabled and current; otherwise <see langword="null"/>.</returns>
    Task<ICapabilityExecutableLaunchFence?> AcquireLaunchFenceAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes one bounded launch operation while final lifecycle validation and its authority fence remain active.</summary>
    /// <typeparam name="TResult">The reference-type launch result.</typeparam>
    /// <param name="operation">The bounded launch operation that must complete before authority is released.</param>
    /// <param name="cancellationToken">The cancellation token used while validating and launching.</param>
    /// <returns>The launch result when lifecycle validation succeeds; otherwise <see langword="null"/>.</returns>
    async Task<TResult?> ExecuteWithLaunchFenceAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var fence = await AcquireLaunchFenceAsync(cancellationToken).ConfigureAwait(false);
        return fence is null ? null : await operation(cancellationToken).ConfigureAwait(false);
    }
}
