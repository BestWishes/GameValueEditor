using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;

namespace GameValueEditor.Services;

public sealed record DownloadProgressSnapshot(long BytesReceived, long? TotalBytes)
{
    public int? Percentage => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100L / TotalBytes.Value, 0, 100)
        : null;

    public string DisplayText
    {
        get
        {
            var received = FormatMegabytes(BytesReceived);
            if (TotalBytes is not > 0) return $"已下载 {received} MB";
            return $"{Percentage}% · {received} / {FormatMegabytes(TotalBytes.Value)} MB";
        }
    }

    private static string FormatMegabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);
}

public sealed record DownloadTimeoutPolicy(TimeSpan TotalTimeout, TimeSpan InactivityTimeout)
{
    public static DownloadTimeoutPolicy Default { get; } =
        new(TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(60));
}

public static class HttpDownloadService
{
    private const int BufferSize = 128 * 1024;
    private const long MinimumReportBytes = 256 * 1024;
    private static readonly TimeSpan MinimumReportInterval = TimeSpan.FromMilliseconds(200);

    public static async Task DownloadToFileAsync(
        HttpClient httpClient,
        string downloadUrl,
        string destinationPath,
        long expectedLength,
        IProgress<DownloadProgressSnapshot>? progress = null,
        DownloadTimeoutPolicy? timeoutPolicy = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("下载地址必须使用 HTTPS。");

        var policy = timeoutPolicy ?? DownloadTimeoutPolicy.Default;
        if (policy.TotalTimeout <= TimeSpan.Zero || policy.InactivityTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeoutPolicy), "下载超时必须大于零。");

        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(policy.TotalTimeout);
        var downloadStarted = false;
        var completed = false;
        try
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                totalTimeout.Token);
            response.EnsureSuccessStatusCode();
            var responseLength = response.Content.Headers.ContentLength;
            if (expectedLength > 0 && responseLength is > 0 && responseLength.Value != expectedLength)
                throw new InvalidOperationException("服务器返回的下载大小与发布记录不一致。");

            var totalLength = expectedLength > 0 ? expectedLength : responseLength;
            progress?.Report(new DownloadProgressSnapshot(0, totalLength));
            await using var source = await response.Content.ReadAsStreamAsync(totalTimeout.Token);
            await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            downloadStarted = true;
            var buffer = new byte[BufferSize];
            var received = 0L;
            var lastReportedBytes = 0L;
            var reportClock = Stopwatch.StartNew();
            while (true)
            {
                int count;
                using (var inactivityTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalTimeout.Token))
                {
                    inactivityTimeout.CancelAfter(policy.InactivityTimeout);
                    try
                    {
                        count = await source.ReadAsync(buffer.AsMemory(), inactivityTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                             !totalTimeout.IsCancellationRequested)
                    {
                        throw new TimeoutException($"连续 {policy.InactivityTimeout.TotalSeconds:0} 秒没有收到下载数据。");
                    }
                }

                if (count == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, count), totalTimeout.Token);
                received += count;
                if (received - lastReportedBytes >= MinimumReportBytes ||
                    reportClock.Elapsed >= MinimumReportInterval)
                {
                    progress?.Report(new DownloadProgressSnapshot(received, totalLength));
                    lastReportedBytes = received;
                    reportClock.Restart();
                }
            }

            await target.FlushAsync(totalTimeout.Token);
            if (expectedLength > 0 && received != expectedLength)
                throw new InvalidOperationException("下载文件大小与发布记录不一致。");
            progress?.Report(new DownloadProgressSnapshot(received, totalLength ?? received));
            completed = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                 totalTimeout.IsCancellationRequested)
        {
            throw new TimeoutException($"下载超过 {policy.TotalTimeout.TotalMinutes:0} 分钟，已自动停止。");
        }
        finally
        {
            if (downloadStarted && !completed)
            {
                try { if (File.Exists(destinationPath)) File.Delete(destinationPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
