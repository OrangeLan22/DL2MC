namespace McInstaller.App.Services;

/// <summary>
/// 启动器目录的校验
/// </summary>
public static class LauncherLocator
{
    /// <summary>校验目录是否可用</summary>
    public static string? Validate(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "请先选择启动器的 .minecraft 目录";
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return "目录不存在";
            }

            // 根目录提示
            if (Directory.Exists(Path.Combine(directory, ".minecraft")))
            {
                return "这看起来是启动器根目录，请选择其下的 .minecraft 子目录";
            }

            return null;
        }
        catch (Exception ex)
        {
            return "无法访问目录：" + ex.Message;
        }
    }
}
