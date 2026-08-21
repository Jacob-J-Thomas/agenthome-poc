using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;

namespace EmbodySense.Core.Application.Credentials.Leases.Models;

/// <summary>Returns exact value-free evidence from one redemption-boundary ordering decision.</summary>
public sealed record CredentialLeaseBoundaryResult(CredentialLeaseBoundaryStatus Status, CredentialLeaseAttemptHistory? History = null);
