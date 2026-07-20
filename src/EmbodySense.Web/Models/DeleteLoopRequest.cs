namespace EmbodySense.Web.Models;

public sealed record DeleteLoopRequest(int ExpectedDefinitionVersion, string OperationId);
