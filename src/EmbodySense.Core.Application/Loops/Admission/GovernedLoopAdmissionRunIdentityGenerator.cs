namespace EmbodySense.Core.Application.Loops.Admission;

/// <summary>Creates collision-resistant canonical identities for newly admitted governed-loop runs.</summary>
public sealed class GovernedLoopAdmissionRunIdentityGenerator : IGovernedLoopAdmissionRunIdentityGenerator
{
    /// <inheritdoc />
    public string CreateRunId() => CustomLoopGeneratedIdentifier.New("run");
}
