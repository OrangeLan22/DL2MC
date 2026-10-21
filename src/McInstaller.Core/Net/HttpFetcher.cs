using System.Net;
using System.Net.Http.Headers;

namespace McInstaller.Core.Net;

/// <summary>一次 HTTP 请求的追踪记录，用于排查下载与列表获取问题。</summary>
public sealed record HttpTrace(
    Uri Uri,
    string Method,
    int? StatusCode,
    TimeSpan Elapsed,
    long? Bytes,
    int Attempt,
    string? Error)
{
    public bool Success => StatusCode is >= 200 and < 300;

    /// <summary>单行摘要，直接可读。</summary>
    public string ToLine()
    {
        var status = StatusCode?.ToString() ?? "ERR";
        var ms = Elapsed.TotalMilliseconds.ToString("F0");
        var size = Bytes is { } b ? $" {b / 1024.0:F1}KB" : "";
        var retry = Attempt > 1 ? $" (第 {Attempt} 次)" : "";
        var err = Error is null ? "" : $" — {Error}";

        // 完整 URL 太长会让日志难读，只保留 host + path
        var shortUri = Uri.IsAbsoluteUri
            ? $"{Uri.Host}{Uri.AbsolutePath}"
            : Uri.ToString();

        return $"[HTTP] {status} {ms}ms {Method} {shortUri}{size}{retry}{err}";
    }
}

/// <summary>
/// HTTP 访问层。全局共享一个 HttpClient，内置幂等失败的指数退避重试。
///
/// 重试原则（见 docs/ENDPOINTS.md §9）：
///   - 只对超时、连接异常、5xx、408 重试
///   - 4xx（除 408）直接失败，重试没有意义
///   - 用户主动取消不重试
/// </summary>
public sealed class HttpFetcher : IDisposable
{
    private readonly HttpClient _http;
    private readonly int _maxRetries;

    /// <summary>每次 HTTP 请求完成后触发（含失败与重试）。用于网络日志。</summary>
    public event Action<HttpTrace>? Traced;

    /// <summary>
    /// </summary>
    /// <param name="maxConnections">每个服务器的最大连接数。</param>
    /// <param name="maxRetries">幂等失败的重试次数。</param>
    /// <param name="timeout">单次请求超时。</param>
    /// <param name="handler">
    /// 可注入的消息处理器，仅用于测试（传假 handler 模拟各种响应）。
    /// 为 null 时使用带连接池的 SocketsHttpHandler。
    /// </param>
    public HttpFetcher(
        int maxConnections = 32,
        int maxRetries = 4,
        TimeSpan? timeout = null,
        HttpMessageHandler? handler = null)
    {
        _maxRetries = maxRetries;

        if (handler is null)
        {
            handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = maxConnections,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(20),
            };
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(5),
        };

        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DownLoad2MC", "0.1"));
    }

    /// <summary>测试用：重试前的最短退避时间。生产环境保持默认。</summary>
    internal TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<string> GetStringAsync(Uri uri, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(uri, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(uri, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把响应流式写入目标路径。目标文件由调用方负责原子落盘（先写 .tmp 再改名）。
    /// </summary>
    public async Task<long> DownloadToFileAsync(Uri uri, string destinationPath, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(uri, ct).ConfigureAwait(false);

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        await source.CopyToAsync(target, ct).ConfigureAwait(false);
        return target.Length;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int? statusCode = null;
            long? contentLength = null;

            try
            {
                var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                watch.Stop();
                statusCode = (int)response.StatusCode;
                contentLength = response.Content.Headers.ContentLength;

                if (response.IsSuccessStatusCode)
                {
                    Traced?.Invoke(new HttpTrace(
                        uri, "GET", statusCode, watch.Elapsed, contentLength, attempt + 1, null));
                    return response;
                }

                var status = response.StatusCode;
                response.Dispose();

                if (!IsRetryableStatus(status))
                {
                    var failure = new NonRetryableHttpException($"请求失败：HTTP {(int)status} {status} — {uri}");
                    Traced?.Invoke(new HttpTrace(
                        uri, "GET", statusCode, watch.Elapsed, contentLength, attempt + 1, failure.Message));
                    throw failure;
                }

                lastError = new HttpRequestException($"HTTP {(int)status} {status} — {uri}");
                Traced?.Invoke(new HttpTrace(
                    uri, "GET", statusCode, watch.Elapsed, contentLength, attempt + 1, $"HTTP {(int)status}"));
            }
            catch (NonRetryableHttpException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                watch.Stop();
                Traced?.Invoke(new HttpTrace(
                    uri, "GET", statusCode, watch.Elapsed, contentLength, attempt + 1, "用户取消"));
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                watch.Stop();
                lastError = ex;
                Traced?.Invoke(new HttpTrace(
                    uri, "GET", statusCode, watch.Elapsed, contentLength, attempt + 1,
                    $"{ex.GetType().Name}: {ex.Message}"));
            }

            if (attempt < _maxRetries)
            {
                // 指数退避：1s, 2s, 4s, 8s（测试可改小 RetryBaseDelay）
                var delay = TimeSpan.FromMilliseconds(
                    RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        var finalError = new HttpRequestException(
            $"重试 {_maxRetries} 次后仍失败：{uri} — {lastError?.Message}", lastError);

        Traced?.Invoke(new HttpTrace(
            uri, "GET", null, TimeSpan.Zero, null, _maxRetries + 1, finalError.Message));

        throw finalError;
    }

    /// <summary>不可重试的 HTTP 失败（4xx 等）。独立类型是为了不被重试逻辑捕获。</summary>
    private sealed class NonRetryableHttpException(string message) : Exception(message);

    private static bool IsRetryableStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return code >= 500 || code == 408 || code == 429;
    }

    public void Dispose() => _http.Dispose();
}
