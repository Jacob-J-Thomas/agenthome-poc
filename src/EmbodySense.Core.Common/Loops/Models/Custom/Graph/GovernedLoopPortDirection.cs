namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Identifies whether a governed loop port receives or produces a typed value.</summary>
public enum GovernedLoopPortDirection
{
    /// <summary>An undefined direction.</summary>
    Unknown = 0,
    /// <summary>An input port.</summary>
    Input,
    /// <summary>An output port.</summary>
    Output
}
