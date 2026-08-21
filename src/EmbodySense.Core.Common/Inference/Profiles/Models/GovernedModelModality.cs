namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Identifies one closed model input or output modality.</summary>
public enum GovernedModelModality
{
    /// <summary>The value is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>Text content.</summary>
    Text = 1,
    /// <summary>Image content.</summary>
    Image = 2,
    /// <summary>Audio content.</summary>
    Audio = 3
}
