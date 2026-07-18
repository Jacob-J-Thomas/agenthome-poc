namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopExecutionLease : IDisposable
{
    string OperationId { get; }
}
