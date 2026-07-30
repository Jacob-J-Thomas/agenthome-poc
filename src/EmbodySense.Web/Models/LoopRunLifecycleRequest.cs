namespace EmbodySense.Web.Models;

/// <summary>
/// Represents an optimistic, idempotent pause, cancel, or resume request for a custom-loop run.
/// </summary>
/// <param name="ExpectedLifecycleVersion">The exact run lifecycle version the caller observed.</param>
/// <param name="OperationId">The caller-generated control-operation identity reused after ambiguous outcomes.</param>
public sealed record LoopRunLifecycleRequest(int ExpectedLifecycleVersion, string OperationId);
