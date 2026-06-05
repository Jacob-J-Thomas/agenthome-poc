namespace AgentHome.Core.Tests;

public sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static TestWorkspace Create()
    {
        var basePath = GetBasePath();
        Directory.CreateDirectory(basePath);

        var root = System.IO.Path.Combine(basePath, $"agenthome-core-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new TestWorkspace(root);
    }

    public string Path(string relativePath)
    {
        return System.IO.Path.Combine(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static string GetBasePath()
    {
        var configured = Environment.GetEnvironmentVariable("AGENTHOME_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return OperatingSystem.IsWindows() && Directory.Exists(@"C:\tmp")
            ? @"C:\tmp"
            : System.IO.Path.GetTempPath();
    }
}
