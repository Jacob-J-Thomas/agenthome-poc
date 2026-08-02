using System.Runtime.InteropServices;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Exposes the exact local host contract and process platform used for capability compatibility decisions.</summary>
public static class CapabilityHostRuntime
{
    /// <summary>Gets the current EmbodySense capability-host contract version.</summary>
    public static CapabilityVersion HostContractVersion { get; } = CreateHostContractVersion();

    /// <summary>Gets the current operating-system and process-architecture tuple.</summary>
    public static CapabilityPlatform Platform { get; } = CreatePlatform();

    private static CapabilityVersion CreateHostContractVersion()
    {
        if (!CapabilityVersion.TryParse("1.0.0", out var version, out var error))
        {
            throw new InvalidOperationException(error?.Message ?? "The current capability-host contract version is invalid.");
        }

        return version!;
    }

    private static CapabilityPlatform CreatePlatform()
    {
        var operatingSystem = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "macos"
                    : OperatingSystem.IsFreeBSD()
                        ? "freebsd"
                        : OperatingSystem.IsBrowser()
                            ? "browser"
                            : throw new PlatformNotSupportedException("The current operating system has no canonical capability-platform token.");
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (!CapabilityPlatform.TryParse($"{operatingSystem}/{architecture}", out var platform, out var error))
        {
            throw new PlatformNotSupportedException(error?.Message ?? "The current process architecture has no canonical capability-platform token.");
        }

        return platform!;
    }
}
