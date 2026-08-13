namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>Identifies the one selected arm of a trigger loop target.</summary>
public enum TriggerLoopTargetKind
{
    /// <summary>No supported target arm was selected.</summary>
    Unknown = 0,

    /// <summary>An exact legacy custom-loop definition is selected.</summary>
    LegacyDefinition = 1,

    /// <summary>An exact published governed-loop revision and authority grant are selected.</summary>
    GovernedPublication = 2
}
