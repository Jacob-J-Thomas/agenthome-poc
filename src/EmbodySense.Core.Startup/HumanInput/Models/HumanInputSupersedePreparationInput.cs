using System.Text.Json;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Describes one bounded successor proposal from a surface without exposing the canonical binding or grant.</summary>
/// <param name="OperationId">The caller-owned operation identity shared with the later commit.</param>
/// <param name="RequestId">The exact target request identity.</param>
/// <param name="ExpectedRequest">The exact immutable target request reference.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic lifecycle version.</param>
/// <param name="ExpectedLifecycleStatus">The exact optimistic lifecycle status token.</param>
/// <param name="Purpose">The bounded successor purpose.</param>
/// <param name="Prompt">The bounded successor prompt.</param>
/// <param name="ResponseSchema">The untrusted successor response-schema JSON.</param>
/// <param name="PrivacyClass">The successor privacy-class token.</param>
/// <param name="ExpiresAtUtc">The proposed successor response deadline.</param>
/// <param name="ResponsePolicy">The untrusted successor response-policy JSON.</param>
public sealed record HumanInputSupersedePreparationInput(
    string OperationId,
    string RequestId,
    HumanInputSurfaceRequestReference? ExpectedRequest,
    long ExpectedLifecycleVersion,
    string ExpectedLifecycleStatus,
    string Purpose,
    string Prompt,
    JsonElement ResponseSchema,
    string PrivacyClass,
    DateTimeOffset ExpiresAtUtc,
    JsonElement ResponsePolicy);
