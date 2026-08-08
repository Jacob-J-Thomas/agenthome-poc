namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Describes the value-free outcome of one trusted credential use.</summary>
public enum CredentialUseOutcome
{
    /// <summary>The trusted callback reported success.</summary>
    Succeeded = 0,
    /// <summary>Failure occurred before external actuation.</summary>
    FailedBeforeActuation = 1,
    /// <summary>Failure occurred after external actuation began.</summary>
    FailedAfterActuation = 2,
    /// <summary>The external side effect cannot be determined safely.</summary>
    OutcomeUncertain = 3
}
