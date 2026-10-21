using System.Diagnostics;
using System.Text;

namespace McInstaller.Core.Java;

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// 子进程执行封装。
///
/// Forge / NeoForge 的 installer 只能靠运行 Java 来安装，因此必须可靠地处理：
///   - 超时（installer 会自行联网拉 maven 依赖，慢时表现为长时间无输出）
///   - 流式输出（否则出问题时用户只看到卡住，日志里什么都没有）
///   - 取消（用户点取消要能真的杀掉子进程树）
/// </summary>
public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stdout)
            {
                stdout.AppendLine(e.Data);
            }

            onOutputLine?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stderr)
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(15);
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            KillTree(process);

            if (!timedOut)
            {
                throw;
            }
        }

        // 等待输出流排空，否则最后几行可能丢失
        try
        {
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 忽略：进程已结束，残留输出丢弃即可
        }

        var exitCode = timedOut ? -1 : SafeExitCode(process);

        string outText;
        string errText;

        lock (stdout)
        {
            outText = stdout.ToString();
        }

        lock (stderr)
        {
            errText = stderr.ToString();
        }

        return new ProcessResult(exitCode, outText, errText, timedOut);
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已自行退出
        }
    }
}
