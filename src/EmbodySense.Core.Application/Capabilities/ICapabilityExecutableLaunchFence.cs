namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Retains final executable lifecycle authority through isolated process launch.</summary>
public interface ICapabilityExecutableLaunchFence : IAsyncDisposable
{
}
