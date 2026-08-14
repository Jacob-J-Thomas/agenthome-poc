namespace EmbodySense.Core.Application.Loops.Admission;

/// <summary>Creates canonical server-owned run identities for newly admitted governed-loop executions.</summary>
public interface IGovernedLoopAdmissionRunIdentityGenerator
{
    /// <summary>Creates one canonical run identity without granting execution authority.</summary>
    /// <returns>A new bounded governed-loop run identifier.</returns>
    string CreateRunId();
}
