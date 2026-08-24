namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Provides the one Windows replacement call used by the native workspace action host.</summary>
public interface IWorkspaceActionWindowsReplacementBoundary
{
    /// <summary>Performs one replacement over the server-derived paths after the host has authenticated their private handles.</summary>
    /// <param name="replacedPath">The absolute path of the retained target being replaced.</param>
    /// <param name="replacementPath">The absolute path of the authenticated private stage.</param>
    /// <param name="backupPath">The absolute path reserved for the displaced before-image witness.</param>
    /// <exception cref="IOException">Thrown when the native replacement does not complete conclusively.</exception>
    void Replace(string replacedPath, string replacementPath, string backupPath);
}
