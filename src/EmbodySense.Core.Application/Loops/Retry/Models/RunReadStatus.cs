namespace EmbodySense.Core.Application.Loops.Retry.Models;

internal enum RunReadStatus
{
    Found,
    NotFound,
    Conflict,
    Unavailable,
}
