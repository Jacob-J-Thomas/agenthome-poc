using EmbodySense.Core.Application.Secrets.Redaction;

namespace EmbodySense.Core.Application.Secrets.Redaction.Models;

/// <summary>
/// Contains one bounded redacted projection and value-free summary evidence.
/// </summary>
/// <typeparam name="TValue">The safe projection type.</typeparam>
/// <param name="Value">The bounded redacted projection.</param>
/// <param name="Summary">Value-free projection evidence.</param>
public sealed record RedactionProjection<TValue>(TValue Value, RedactionProjectionSummary Summary);
