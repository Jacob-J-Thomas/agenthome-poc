namespace EmbodySense.Core.Application.HumanInput.Publication.Models;

/// <summary>Defines closed checkpoint-to-request-ledger publication dispositions.</summary>
public enum HumanInputRequestPublicationStatus
{
    /// <summary>No supported disposition was established.</summary>
    Unknown = 0,

    /// <summary>The checkpoint's exact request Create operation committed durably.</summary>
    Published = 1,

    /// <summary>The checkpoint's exact request Create operation was already durable and replayed.</summary>
    Replayed = 2,

    /// <summary>The caller's scanned checkpoint no longer exists or retains a different immutable hash.</summary>
    Stale = 3,

    /// <summary>Current authority, storage, or durable outcome evidence was temporarily unavailable or ambiguous.</summary>
    Unavailable = 4,

    /// <summary>The run, checkpoint, admission, or lifecycle evidence was invalid, divergent, or unsafe to publish.</summary>
    Corrupt = 5,
}
