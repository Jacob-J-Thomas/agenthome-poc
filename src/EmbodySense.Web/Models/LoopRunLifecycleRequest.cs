namespace EmbodySense.Web.Models;

public sealed record LoopRunLifecycleRequest(int ExpectedLifecycleVersion, string OperationId);
