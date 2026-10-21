using System.Runtime.InteropServices;

namespace McInstaller.Core.Pipeline;

/// <summary>
/// 当前运行平台信息。字段取值必须与 Minecraft 版本 JSON 中的 os.name / os.arch 一致。
/// 注意 macOS 在 JSON 里叫 "osx"，不是 "macos"。
/// </summary>
public sealed record PlatformInfo(string OsName, string OsArch)
{
    public static PlatformInfo Current { get; } = Detect();

    private static PlatformInfo Detect()
    {
        string os;
        if (OperatingSystem.IsWindows())
        {
            os = "windows";
        }
        else if (OperatingSystem.IsMacOS())
        {
            os = "osx";
        }
        else
        {
            os = "linux";
        }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm32",
            _ => "unknown",
        };

        return new PlatformInfo(os, arch);
    }
}
