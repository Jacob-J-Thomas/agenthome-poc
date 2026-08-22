namespace EmbodySense.Core.Persistence.Inference.Profiles;

internal sealed class ModelProfileMetadataStorePaths
{
    internal ModelProfileMetadataStorePaths(string serverStateRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverStateRootPath);
        ServerStateRootPath = Path.GetFullPath(serverStateRootPath);
        RootPath = Path.Combine(ServerStateRootPath, "model-profile-metadata");
        PrimaryPath = Path.Combine(RootPath, "catalog.json");
        ProofPath = Path.Combine(RootPath, "catalog.proved.json");
        LockPath = Path.Combine(RootPath, ".catalog.lock");
        TrustRootPath = Path.Combine(ServerStateRootPath, "model-profile-metadata-trust");
    }

    internal string ServerStateRootPath { get; }
    internal string RootPath { get; }
    internal string PrimaryPath { get; }
    internal string ProofPath { get; }
    internal string LockPath { get; }
    internal string TrustRootPath { get; }
}
