using Avalonia;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace McInstaller.App;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 未处理异常至少留下痕迹 —— 否则崩溃时窗口直接消失，无从排查
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(e.ExceptionObject as Exception, "AppDomain");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "UnobservedTask");
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Main");
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>
/// 崩溃日志。写到程序目录下的 logs\crash-时间.log。
/// </summary>
internal static class CrashLog
{
    public static void Write(Exception? ex, string source)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"时间   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"来源   : {source}");
            sb.AppendLine($"异常   : {ex?.GetType().FullName ?? "(null)"}");
            sb.AppendLine($"消息   : {ex?.Message}");
            sb.AppendLine();
            sb.AppendLine("--- 堆栈 ---");
            sb.AppendLine(ex?.ToString() ?? "(无)");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 记录崩溃本身失败
        }
    }
}
