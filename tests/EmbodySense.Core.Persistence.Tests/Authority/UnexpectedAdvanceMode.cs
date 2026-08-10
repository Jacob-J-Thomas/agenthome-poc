namespace EmbodySense.Core.Persistence.Tests.Authority;

public enum UnexpectedAdvanceMode
{
    NoOp,
    Stale,
    WrongWorkspace,
    WrongCurrentGeneration,
    WrongCurrentDigest,
    WrongPreviousGeneration,
    WrongPreviousDigest
}
