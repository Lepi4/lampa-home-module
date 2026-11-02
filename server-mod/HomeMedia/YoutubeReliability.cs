using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;

namespace HomeMedia;

public sealed class YoutubeFailure : Exception
{
    public string Code { get; }
    public bool Retryable => Code is "network_error" or "extract_timeout" or "busy";
    public int Status => Code == "unavailable" ? 404 : Code == "rate_limited" ? 429 : Retryable || Code == "verification_required" ? 503 : 502;
    public YoutubeFailure(string code) : base(code) { Code = code; }
    public string UserMessage => Code switch {
        "verification_required" => "YouTube требует проверку подключения. Попробуйте позже.",
        "unavailable" => "Видео удалено, закрыто или ограничено владельцем.",
        "rate_limited" => "YouTube временно ограничил запросы. Попробуйте позже.",
        "busy" => "Сервер занят подготовкой других видео. Попробуйте ещё раз.",
        "extract_timeout" => "Не удалось подготовить видео вовремя. Попробуйте ещё раз.",
        "network_error" => "Нет связи с YouTube. Попробуйте ещё раз.",
        _ => "У видео нет доступного поддерживаемого потока."
    };
    public static string Classify(string text)
    {
        text = (text ?? "").ToLowerInvariant();
        if (text.Contains("not a bot") || text.Contains("confirm you") || text.Contains("po token")) return "verification_required";
        if (text.Contains("429") || text.Contains("too many requests")) return "rate_limited";
        if (text.Contains("private video") || text.Contains("video unavailable") || text.Contains("has been removed") || text.Contains("not available in your country") || text.Contains("age-restricted") || text.Contains("members-only")) return "unavailable";
        if (text.Contains("timed out") || text.Contains("timeout") || text.Contains("connection") || text.Contains("unable to download") || text.Contains("resolve")) return "network_error";
        return "no_stream";
    }
}

// One extraction per video/route; a departing waiter never cancels another viewer.
public sealed class YoutubeFlights<T>
{
    sealed class Flight { public CancellationTokenSource Cancel = new(); public Task<T> Task; public int Users; }
    readonly object gate = new();
    readonly Dictionary<string, Flight> jobs = new();
    public async Task<T> Get(string key, Func<CancellationToken, Task<T>> factory, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        Flight job;
        lock (gate) {
            if (!jobs.TryGetValue(key, out job)) {
                job = new Flight(); jobs[key] = job;
                try { job.Task = factory(job.Cancel.Token); } catch (Exception e) { job.Task = Task.FromException<T>(e); }
            }
            job.Users++;
        }
        try { return await job.Task.WaitAsync(caller); }
        finally {
            bool last;
            lock (gate) { last = --job.Users == 0; if (last && jobs.TryGetValue(key, out var current) && ReferenceEquals(current, job)) jobs.Remove(key); }
            if (last) {
                if (!job.Task.IsCompleted) job.Cancel.Cancel();
                _ = job.Task.ContinueWith(_ => job.Cancel.Dispose(), TaskScheduler.Default);
            }
        }
    }
}

public static class YoutubeProcess
{
    static readonly SemaphoreSlim slots = new(3);
    public sealed record Result(string Output, string Error);
    public static async Task<Result> Run(string bin, string proxy, int seconds, CancellationToken caller, params string[] args)
    {
        if (!await slots.WaitAsync(TimeSpan.FromSeconds(10), caller)) return new(null, "busy");
        try {
            var psi = new ProcessStartInfo(bin) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            psi.Environment["PYTHONUTF8"] = "1"; psi.Environment["LC_ALL"] = "C.UTF-8";
            if (!string.IsNullOrWhiteSpace(proxy)) { psi.ArgumentList.Add("--proxy"); psi.ArgumentList.Add(proxy); }
            foreach (var a in new[] { "--js-runtimes", "quickjs", "--no-warnings", "--no-cache-dir", "--socket-timeout", "10", "--retries", "0", "--extractor-retries", "0" }) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(caller); budget.CancelAfter(TimeSpan.FromSeconds(seconds));
            try { await process.WaitForExitAsync(budget.Token); }
            catch (OperationCanceledException) {
                try { process.Kill(true); await process.WaitForExitAsync(); } catch { }
                await Task.WhenAll(stderr, stdout);
                caller.ThrowIfCancellationRequested(); return new(null, "extract_timeout");
            }
            return process.ExitCode == 0 ? new(await stdout, null) : new(null, YoutubeFailure.Classify(await stderr));
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(null, "network_error"); }
        finally { slots.Release(); }
    }
}

public static class YoutubeLinks
{
    public static bool Fresh(JObject info, string route, long now)
    {
        if (info == null || (string)info["proxy_stamp"] != route || info["formats"] is not JArray formats || formats.Count == 0) return false;
        return formats.Children<JObject>().All(f => {
            var m = Regex.Match((string)f["url"] ?? "", @"[?&]expire=(\d+)");
            return !m.Success || long.TryParse(m.Groups[1].Value, out long expires) && expires > now + 120;
        });
    }
}

// Testable relay: only validated successful ranges ever reach the response body.
public sealed class YoutubeRelay
{
    readonly Func<Uri, long, long, CancellationToken, Task<HttpResponseMessage>> fetch;
    readonly Func<CancellationToken, Task<Uri>> renew;
    readonly long chunk;
    public YoutubeRelay(Func<Uri, long, long, CancellationToken, Task<HttpResponseMessage>> fetch, Func<CancellationToken, Task<Uri>> renew = null, long chunk = 8 * 1024 * 1024) { this.fetch = fetch; this.renew = renew; this.chunk = chunk; }
    static bool Temporary(int status) => status is 408 or 429 || status >= 500;
    public async Task Send(HttpContext context, Uri uri)
    {
        var caller = context.RequestAborted;
        string header = context.Request.Headers["Range"].ToString();
        bool ranged = !string.IsNullOrEmpty(header), suffix = false;
        long start = 0, end = long.MaxValue, total = -1;
        if (ranged) {
            var m = Regex.Match(header, @"^bytes=(\d*)-(\d*)$");
            if (!m.Success || m.Groups[1].Length + m.Groups[2].Length == 0 ||
                (m.Groups[1].Length > 0 && !long.TryParse(m.Groups[1].Value, out start)) ||
                (m.Groups[2].Length > 0 && !long.TryParse(m.Groups[2].Value, out end))) { context.Response.StatusCode = 416; return; }
            suffix = m.Groups[1].Length == 0;
            if (suffix ? end <= 0 : end < start) { context.Response.StatusCode = 416; return; }
        }
        bool renewed = false, started = false;
        int failures = 0;
        long next = start;
        while (!caller.IsCancellationRequested) {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(caller); budget.CancelAfter(TimeSpan.FromSeconds(45));
            try {
                long requestFrom = suffix ? 0 : next;
                long requestTo = suffix ? 0 : Math.Min(end, next > long.MaxValue - chunk ? long.MaxValue : next + chunk - 1);
                using var response = await fetch(uri, requestFrom, requestTo, budget.Token);
                int status = (int)response.StatusCode;
                if (status is 403 or 410 or 404 && !renewed && renew != null) {
                    renewed = true;
                    // May require a fresh extraction; use its own bounded request lifetime.
                    uri = await renew(caller); continue;
                }
                if (status == 416) {
                    if (!started) { context.Response.StatusCode = 416; if (response.Content.Headers.ContentRange?.Length is long length) context.Response.Headers["Content-Range"] = "bytes */" + length; }
                    else context.Abort();
                    return;
                }
                if (status != 206) {
                    if (Temporary(status) && ++failures <= 2) { await Task.Delay(failures * 350, caller); continue; }
                    if (started) context.Abort(); else context.Response.StatusCode = status >= 400 ? status : 502;
                    return;
                }
                var range = response.Content.Headers.ContentRange;
                if (range?.From != requestFrom || range.To == null || range.To > requestTo || range.Length == null || range.Length <= 0 || range.To >= range.Length || total > 0 && total != range.Length) throw new IOException("invalid_upstream_range");
                total = range.Length.Value;
                if (suffix) { start = Math.Max(0, total - end); next = start; end = total - 1; suffix = false; continue; }
                end = Math.Min(end, total - 1);
                if (next >= total) { context.Response.StatusCode = 416; context.Response.Headers["Content-Range"] = "bytes */" + total; return; }
                if (!started) {
                    context.Response.StatusCode = ranged ? 206 : 200;
                    context.Response.ContentLength = end - start + 1;
                    context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                    context.Response.Headers["Accept-Ranges"] = "bytes";
                    if (ranged) context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
                    started = true;
                }
                long expectedEnd = range.To.Value;
                await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
                var bytes = new byte[81920];
                while (next <= expectedEnd) {
                    int count = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, expectedEnd - next + 1)), budget.Token);
                    if (count == 0) throw new IOException("upstream_truncated");
                    await context.Response.Body.WriteAsync(bytes.AsMemory(0, count), caller);
                    next += count;
                }
                failures = 0;
                if (next > end) return;
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { return; }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) {
                if (++failures <= 2) { await Task.Delay(failures * 350, caller); continue; }
                if (started) context.Abort(); else context.Response.StatusCode = 502;
                return;
            }
            catch {
                if (started) context.Abort(); else context.Response.StatusCode = 502;
                return;
            }
        }
    }
}
