namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Defines one selectable, data-only choice.
/// </summary>
/// <param name="ChoiceId">The stable choice ID.</param>
/// <param name="DisplayText">The bounded canonical display text.</param>
public sealed record HumanInputChoice(string ChoiceId, string DisplayText);
