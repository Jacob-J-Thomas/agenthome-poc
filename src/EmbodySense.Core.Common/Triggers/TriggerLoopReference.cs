using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Identifies exactly one legacy custom-loop definition or one published governed-loop revision and authority grant.
/// </summary>
/// <remarks>The reference is factory-created identity evidence only. It does not grant authority or prove that the target is currently executable.</remarks>
public sealed record TriggerLoopReference
{
    internal TriggerLoopReference(TriggerLoopTargetKind kind, TriggerLegacyLoopDefinitionReference? legacyDefinition, GovernedLoopRevisionPublicationPin? governedPublication, AuthorityGrantReference? authorityGrant)
    {
        Kind = kind;
        LegacyDefinition = legacyDefinition;
        GovernedPublication = governedPublication;
        AuthorityGrant = authorityGrant;
    }

    /// <summary>Gets the closed target family discriminator.</summary>
    public TriggerLoopTargetKind Kind { get; }

    /// <summary>Gets the exact legacy definition arm, present only for <see cref="TriggerLoopTargetKind.LegacyDefinition"/>.</summary>
    public TriggerLegacyLoopDefinitionReference? LegacyDefinition { get; }

    /// <summary>Gets the exact published governed-loop revision, present only for <see cref="TriggerLoopTargetKind.GovernedPublication"/>.</summary>
    public GovernedLoopRevisionPublicationPin? GovernedPublication { get; }

    /// <summary>Gets the exact revision-pinned grant, present only for <see cref="TriggerLoopTargetKind.GovernedPublication"/>.</summary>
    public AuthorityGrantReference? AuthorityGrant { get; }

    /// <summary>Gets the stable loop identifier derived from the selected arm without storing competing target truth.</summary>
    public string LoopId => Kind switch
    {
        TriggerLoopTargetKind.LegacyDefinition => LegacyDefinition?.LoopId ?? string.Empty,
        TriggerLoopTargetKind.GovernedPublication => GovernedPublication?.Revision?.GraphId ?? string.Empty,
        _ => string.Empty
    };

    /// <summary>Gets the legacy definition version, or <see langword="null"/> for a governed publication.</summary>
    public int? DefinitionVersion => LegacyDefinition?.DefinitionVersion;

    /// <summary>Gets the legacy definition content hash, or <see langword="null"/> for a governed publication.</summary>
    public string? ContentHash => LegacyDefinition?.ContentHash;
}
