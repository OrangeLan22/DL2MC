namespace McInstaller.Core.Util;

/// <summary>
/// Maven 坐标解析：group:artifact:version[:classifier][@ext]
/// 例如：
///   org.ow2.asm:asm:9.10.1
///   org.lwjgl:lwjgl:3.3.3:natives-windows
///   net.minecraft:client:1.21.11@jar
/// </summary>
public sealed record MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    public static MavenCoordinate Parse(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Maven 坐标为空", nameof(name));
        }

        var extension = "jar";
        var atIndex = name.LastIndexOf('@');
        if (atIndex >= 0)
        {
            extension = name[(atIndex + 1)..];
            name = name[..atIndex];
        }

        var parts = name.Split(':');
        if (parts.Length < 3)
        {
            throw new FormatException($"非法 Maven 坐标：{name}");
        }

        var classifier = parts.Length >= 4 ? parts[3] : null;

        return new MavenCoordinate(parts[0], parts[1], parts[2], classifier, extension);
    }

    /// <summary>
    /// 仓库内相对路径，如 org/ow2/asm/asm/9.10.1/asm-9.10.1.jar
    /// </summary>
    public string ToRelativePath()
    {
        var groupPath = Group.Replace('.', '/');
        var fileName = $"{Artifact}-{Version}";
        if (!string.IsNullOrEmpty(Classifier))
        {
            fileName += $"-{Classifier}";
        }

        return $"{groupPath}/{Artifact}/{Version}/{fileName}.{Extension}";
    }
}
