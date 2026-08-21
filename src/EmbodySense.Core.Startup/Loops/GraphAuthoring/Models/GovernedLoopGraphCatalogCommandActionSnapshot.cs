namespace EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

/// <summary>Projects safe server-owned command-template identity, availability, and resource limits without process or artifact locators.</summary>
public sealed record GovernedLoopGraphCatalogCommandActionSnapshot(
    string TemplateId,
    long TemplateVersion,
    string TemplateHash,
    string Availability,
    bool RequiresCredentialChannel,
    string WorkingDirectory,
    string Network,
    int MaxExecutionMilliseconds,
    int MaxTerminationMilliseconds,
    long MaxMemoryBytes,
    int MaxOutputBytes,
    int MaxConcurrency,
    bool RequiresProcessTreeTermination);
