namespace EmbodySense.Core.Persistence.ContextualRoles;

internal sealed class ContextualRolePublicationAmbiguousException : IOException
{
    public ContextualRolePublicationAmbiguousException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
