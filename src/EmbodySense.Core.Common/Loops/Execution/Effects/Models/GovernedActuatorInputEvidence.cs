namespace EmbodySense.Core.Common.Loops.Execution.Effects.Models;

/// <summary>Contains bounded canonical in-memory actuator input and its domain-separated fingerprint.</summary>
/// <param name="CanonicalJson">The canonical compact JSON retained only for immediate structured dispatch.</param>
/// <param name="Fingerprint">The lowercase SHA-256 input fingerprint retained in durable intent.</param>
/// <param name="Utf8ByteCount">The exact canonical UTF-8 byte count.</param>
/// <param name="ElementCount">The bounded JSON value and property count.</param>
public sealed record GovernedActuatorInputEvidence(
    string CanonicalJson,
    string Fingerprint,
    int Utf8ByteCount,
    int ElementCount);
