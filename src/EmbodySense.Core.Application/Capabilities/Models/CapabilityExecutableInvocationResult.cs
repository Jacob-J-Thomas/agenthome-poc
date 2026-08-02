namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns bounded, redacted evidence from one isolated invocation.</summary>
/// <param name="Status">The invocation status.</param>
/// <param name="OperationId">The invocation identity.</param>
/// <param name="OutputJson">The bounded JSON result on success.</param>
/// <param name="Diagnostic">The bounded redacted diagnostic.</param>
/// <param name="ExitCode">The process exit code when observed.</param>
/// <param name="Duration">The observed invocation duration.</param>
public sealed record CapabilityExecutableInvocationResult(CapabilityExecutableInvocationStatus Status, string OperationId, string? OutputJson, string Diagnostic, int? ExitCode, TimeSpan Duration);
