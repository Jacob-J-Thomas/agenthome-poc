namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal enum ScriptedCredentialStoreStatus
{
    Success = 0,
    Missing = 1,
    Unavailable = 2,
    Corrupt = 3,
    LimitExceeded = 4
}
