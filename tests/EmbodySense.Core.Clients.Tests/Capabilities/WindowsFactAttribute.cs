namespace EmbodySense.Core.Clients.Tests.Capabilities;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "This executable-isolation contract requires the Windows retained-handle launch boundary.";
        }
    }
}
