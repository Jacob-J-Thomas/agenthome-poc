namespace EmbodySense.Tests.Support;

public sealed class TestWorkspace : IDisposable
{
    private const string DefaultCapabilityCatalogTrustRootEnvironmentVariable = "EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT";
    private static readonly string? _previousDefaultCapabilityCatalogTrustRoot = Environment.GetEnvironmentVariable(DefaultCapabilityCatalogTrustRootEnvironmentVariable);
    private static readonly string _defaultCapabilityCatalogTrustRoot = Path.Combine(PhysicalTempPath(), "embodysense-test-default-capability-catalog", $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    static TestWorkspace()
    {
        // Ephemeral test workspaces must never consume the durable production-default root, whose anchors are intentionally monotonic. https://github.com/Jacob-J-Thomas/agenthome-poc/issues/495
        Environment.SetEnvironmentVariable(DefaultCapabilityCatalogTrustRootEnvironmentVariable, _defaultCapabilityCatalogTrustRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreDefaultCapabilityCatalogTrustRoot();
    }

    public TestWorkspace()
    {
        var identifier = Guid.NewGuid().ToString("N");
        var tempPath = PhysicalTempPath();
        RootPath = System.IO.Path.Combine(tempPath, "embodysense-tests", identifier);
        var serverStateParentPath = System.IO.Path.Combine(tempPath, "embodysense-test-server-state");
        // Parallel fixtures may safely create unique guarded roots only after their shared test-owned parent exists.
        Directory.CreateDirectory(serverStateParentPath);
        ServerStatePath = System.IO.Path.Combine(serverStateParentPath, identifier);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string ServerStatePath { get; }

    private static string PhysicalTempPath()
    {
        var tempPath = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        return OperatingSystem.IsMacOS() && (string.Equals(tempPath, "/var", StringComparison.Ordinal) || tempPath.StartsWith("/var/", StringComparison.Ordinal)) ? "/private" + tempPath : tempPath;
    }

    public string File(params string[] segments)
    {
        return System.IO.Path.Combine([RootPath, .. segments]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            if (Directory.Exists(ServerStatePath))
            {
                Directory.Delete(ServerStatePath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RestoreDefaultCapabilityCatalogTrustRoot()
    {
        try
        {
            Environment.SetEnvironmentVariable(DefaultCapabilityCatalogTrustRootEnvironmentVariable, _previousDefaultCapabilityCatalogTrustRoot);
            if (Directory.Exists(_defaultCapabilityCatalogTrustRoot))
            {
                Directory.Delete(_defaultCapabilityCatalogTrustRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
