namespace EmbodySense.Core.Persistence.Credentials;

internal interface IWindowsCredentialStore
{
    bool IsSupported { get; }
    int MaxValueByteLength { get; }
    WindowsCredentialStoreStatus Probe(string target);
    WindowsCredentialReadResult Read(string target);
    WindowsCredentialStoreStatus Write(string target, byte[] value);
    WindowsCredentialStoreStatus Delete(string target);
}
