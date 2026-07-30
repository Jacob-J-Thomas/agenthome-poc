using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Reports that runtime composition could not resolve a Codex executable compatible with the configured model.
/// </summary>
public sealed class CodexRuntimeUnavailableException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CodexRuntimeUnavailableException"/> type.
    /// </summary>
    /// <param name="status">The incompatible resolution result used to build the diagnostic message.</param>
    public CodexRuntimeUnavailableException(CodexRuntimeStatus status) : base(CreateMessage(status))
    {
        ArgumentNullException.ThrowIfNull(status);

        Status = status;
    }

    /// <summary>
    /// Gets the complete incompatible Codex resolution result.
    /// </summary>
    public CodexRuntimeStatus Status { get; }

    private static string CreateMessage(CodexRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var executable = string.IsNullOrWhiteSpace(status.ResolvedExecutablePath) ? "(not found)" : status.ResolvedExecutablePath;
        var version = string.IsNullOrWhiteSpace(status.Version) ? "(unknown)" : status.Version;
        var model = string.IsNullOrWhiteSpace(status.ConfiguredModel) ? "(configured externally)" : status.ConfiguredModel;
        return $"Codex runtime is not usable. Executable: {executable}. Version: {version}. Model: {model}. {status.Detail}";
    }
}
