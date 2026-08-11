namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Identifies a bounded declarative completion boundary without observing runtime completion.</summary>
public enum AuthorityGrantCompletionConstraintKind
{
    /// <summary>The completion constraint is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>No run-completion boundary is declared.</summary>
    None = 1,
    /// <summary>The grant becomes ineffective after the first exact bound run completion observed by a later runtime owner.</summary>
    FirstBoundRunCompletion = 2,
}
