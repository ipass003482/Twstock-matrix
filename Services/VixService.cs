using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StockMatrix.Services;

public record VixSnapshot(double? UsVix, double? TwVix, string UpdatedAt);

public class VixService
{
    private readonly HttpClient _http;
    private readonly ILogger<VixService> _log;

    // Cache TW VIX from TAIFEX for 5 minutes
    private static (double? value, DateTime fetchedAt) _twVixCache;
    private static readonly SemaphoreSlim _twLock = new(1, 1);
    private static readonly TimeSpan TwVixTtl = TimeSpan.FromMinutes(5);

    public VixService(HttpClient http, ILogger<VixService> log)
    {
        _http = http;
        _log  = log;
    }

    public async Task<VixSnapshot> GetAsync()
    {
        var usTask = FetchUsVixAsync();
        var twTask = FetchTwVixAsync();
        await Task.WhenAll(usTask, twTask);
        return new VixSnapshot(await usTask, await twTask,
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC");
    }

    // ── US VIX via stooq.com (fallback: Yahoo Finance) ────────────────
    private async Task<double?> FetchUsVixAsync()
    {
        try
        {
            var end   = DateTime.Today.ToString("yyyyMMdd");
            var start = DateTime.Today.AddDays(-10).ToString("yyyyMMdd");
            var url   = $"https://stooq.com/q/d/l/?s=%5Evix&d1={start}&d2={end}&i=d";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var csv = await resp.Content.ReadAsStringAsync();
                var val = ParseStooqCsv(csv);
                if (val.HasValue) return val;
            }
        }
        catch { /* fallthrough to Yahoo */ }

        return await FallbackYahooVixAsync();
    }

    private async Task<double?> FallbackYahooVixAsync()
    {
        try
        {
            var url = "https://query1.finance.yahoo.com/v8/finance/chart/%5EVIX?range=5d&interval=1d";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            req.Headers.Add("Accept", "application/json");

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var closes = doc.RootElement
                .GetProperty("chart")
                .GetProperty("result")[0]
                .GetProperty("indicators")
                .GetProperty("quote")[0]
                .GetProperty("close");

            double? last = null;
            foreach (var v in closes.EnumerateArray())
                if (v.ValueKind != JsonValueKind.Null)
                    last = Math.Round(v.GetDouble(), 2);

            return last;
        }
        catch { return null; }
    }

    // ── Taiwan VIX via TAIFEX official daily download (free, no auth) ──
    private async Task<double?> FetchTwVixAsync()
    {
        if (_twVixCache.value.HasValue &&
            DateTime.UtcNow - _twVixCache.fetchedAt < TwVixTtl)
        {
            return _twVixCache.value;
        }

        await _twLock.WaitAsync();
        try
        {
            if (_twVixCache.value.HasValue &&
                DateTime.UtcNow - _twVixCache.fetchedAt < TwVixTtl)
            {
                return _twVixCache.value;
            }

            var value = await DownloadTaifexVixAsync();
            if (value.HasValue)
            {
                _twVixCache = (value, DateTime.UtcNow);
                _log.LogInformation("TwVix fetched from TAIFEX: {Value}", value);
            }
            else
            {
                _log.LogWarning("TwVix download returned null");
            }
            return value;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TwVix download failed");
            return null;
        }
        finally { _twLock.Release(); }
    }

    private async Task<double?> DownloadTaifexVixAsync()
    {
        // TAIFEX publishes monthly txt files: e.g. 202604new.txt for April 2026.
        // Format (Big5, tab-separated):
        //   交易日期\t時間\t臺指選擇權波動率指數\t收盤前1分鐘平均指數
        //   20260423\t13450000\t32.81\t32.48
        // We try the current-month file first, fall back to previous month if empty.
        var twNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Taipei Standard Time" : "Asia/Taipei"));

        foreach (var month in new[] { twNow, twNow.AddMonths(-1) })
        {
            var fileName = $"{month:yyyyMM}new.txt";
            var url = $"https://www.taifex.com.tw/file/taifex/Dailydownload/vix/log2data/{fileName}";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                // File is Big5 encoded but only the header has Chinese; data lines are ASCII.
                var text = Encoding.ASCII.GetString(bytes);

                var lastVal = ParseLastVixValue(text);
                if (lastVal.HasValue) return lastVal;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "TAIFEX VIX file {File} failed", fileName);
            }
        }
        return null;
    }

    private static double? ParseLastVixValue(string text)
    {
        // Pick the LAST line that starts with 8-digit date and has a decimal value.
        // Columns: date(yyyymmdd) time(hhmmssms) vixIndex closingMinAvg
        double? last = null;
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var m = Regex.Match(line,
                @"^(?<date>\d{8})\s+\d+\s+(?<vix>\d+\.\d+)");
            if (!m.Success) continue;
            if (double.TryParse(m.Groups["vix"].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) && v > 0)
            {
                last = Math.Round(v, 2);
            }
        }
        return last;
    }

    // ── CSV parser for stooq format: Date,Open,High,Low,Close,Volume ──
    private static double? ParseStooqCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;

        var lastLine = lines[^1];
        var parts    = lastLine.Split(',');
        if (parts.Length >= 5 &&
            double.TryParse(parts[4],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var close))
        {
            return Math.Round(close, 2);
        }
        return null;
    }
}
