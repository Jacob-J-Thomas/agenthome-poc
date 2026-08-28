namespace EmbodySense.Core.Clients.Tests.Capabilities;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            // TODO(https://github.com/Jacob-J-Thomas/agenthome-poc/issues/420): qualify this retained-handle contract on non-Windows verifier hosts.
            Skip = "This executable-isolation contract requires the Windows retained-handle launch boundary.";
        }
    }
}
