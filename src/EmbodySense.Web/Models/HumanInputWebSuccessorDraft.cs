using System.Text.Json;

namespace EmbodySense.Web.Models;

/// <summary>Supplies only untrusted successor content; Startup derives binding, eligibility, continuation, and grant.</summary>
/// <param name="Purpose">The bounded successor purpose.</param>
/// <param name="Prompt">The bounded successor prompt.</param>
/// <param name="ResponseSchema">The successor response-schema JSON.</param>
/// <param name="PrivacyClass">The successor privacy-class token.</param>
/// <param name="ExpiresAtUtc">The proposed UTC successor deadline.</param>
/// <param name="ResponsePolicy">The successor response-policy JSON.</param>
public sealed record HumanInputWebSuccessorDraft(string Purpose, string Prompt, JsonElement ResponseSchema, string PrivacyClass, DateTimeOffset ExpiresAtUtc, JsonElement ResponsePolicy);
