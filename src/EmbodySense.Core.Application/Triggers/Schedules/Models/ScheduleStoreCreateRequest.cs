using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Requests atomic creation of one immutable definition and its exact initial state.</summary>
public sealed record ScheduleStoreCreateRequest(
    ScheduleDefinition Definition,
    ScheduleState InitialState,
    string CanonicalDefinitionHash);
