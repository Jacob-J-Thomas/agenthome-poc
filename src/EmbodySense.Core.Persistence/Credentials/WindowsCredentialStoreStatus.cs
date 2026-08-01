namespace EmbodySense.Core.Persistence.Credentials;

internal enum WindowsCredentialStoreStatus
{
    Success = 0,
    Missing = 1,
    Unavailable = 2,
    Corrupt = 3,
    LimitExceeded = 4
}
