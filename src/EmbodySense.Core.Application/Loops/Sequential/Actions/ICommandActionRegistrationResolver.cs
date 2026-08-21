using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential.Actions;

/// <summary>Resolves exact hash-pinned command Action descriptors from one finite server-owned registry.</summary>
public interface ICommandActionRegistrationResolver
{
    /// <summary>Resolves one exact descriptor without selecting a fallback or newer template revision.</summary>
    bool TryResolve(GovernedLoopNodeDescriptor descriptor, out CommandActionRegistration? registration);
}
