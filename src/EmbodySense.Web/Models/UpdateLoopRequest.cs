using EmbodySense.Core.Startup.Loops;

namespace EmbodySense.Web.Models;

public sealed record UpdateLoopRequest(int ExpectedDefinitionVersion, string OperationId, LoopDefinitionInput Definition);
