namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopControlOperationLease : IDisposable
{
    string OperationId { get; }

    string OwnerGenerationId { get; }
}
