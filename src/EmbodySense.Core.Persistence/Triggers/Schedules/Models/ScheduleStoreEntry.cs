using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Persistence.Triggers.Schedules.Models;

/// <summary>Binds one immutable definition to its current exact optimistic state and canonical hashes.</summary>
internal sealed record ScheduleStoreEntry(
    ScheduleDefinition Definition,
    string DefinitionHash,
    ScheduleState State,
    string StateHash);
