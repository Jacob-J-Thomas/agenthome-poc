using System.Diagnostics;

namespace EmbodySense.Core.Clients.CommandActions.Models;

/// <summary>Returns a process only after every registered control is effective, or proves no process started.</summary>
/// <param name="Status">The closed launch status.</param>
/// <param name="Process">The exact contained root process only when launch succeeded.</param>
public sealed record CommandActionIsolatedLaunchResult(CommandActionIsolatedLaunchStatus Status, Process? Process);
