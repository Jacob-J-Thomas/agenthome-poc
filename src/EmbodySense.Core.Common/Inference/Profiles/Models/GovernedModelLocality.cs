namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Identifies where model inference executes.</summary>
public enum GovernedModelLocality
{
    /// <summary>The locality is unknown and therefore ineligible.</summary>
    Unknown = 0,
    /// <summary>Inference executes on the same device without a separate process.</summary>
    OnDevice = 1,
    /// <summary>Inference executes in another local process.</summary>
    LocalProcess = 2,
    /// <summary>Inference executes on the local network.</summary>
    LocalNetwork = 3,
    /// <summary>Inference executes through a remote service.</summary>
    Remote = 4
}
