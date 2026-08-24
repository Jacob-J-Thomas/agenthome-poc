namespace EmbodySense.Core.Clients.CommandActions;

internal sealed class CommandActionOutputLimitException : Exception
{
    internal CommandActionOutputLimitException() : base("The combined command output exceeded its registered bound.")
    {
    }
}
