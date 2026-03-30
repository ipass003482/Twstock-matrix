using System.Text.Json;

namespace StockMatrix.Services;

public record VixSnapshot(double? UsVix, double? TwVix, string UpdatedAt);

public class VixService
{
    private readonly HttpClient _http;

    public VixService(HttpClient http)
    {
        _http = http;
    }

    public async Task<VixSnapshot> GetAsync()
    {
        var us = await FetchUsVixAsync();
        var tw = await FetchTwVixAsync();
        return new VixSnapshot(us, tw, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC");
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

    // ── Taiwan VIX via Finmind TaiwanVarianceIndex ────────────────────
    private async Task<double?> FetchTwVixAsync()
    {
        try
        {
            var start = DateTime.Today.AddDays(-14).ToString("yyyy-MM-dd");
            var end   = DateTime.Today.ToString("yyyy-MM-dd");
            var url   = $"https://api.finmindtrade.com/api/v4/data?dataset=TaiwanVarianceIndex&data_id=TWVIX&start_date={start}&end_date={end}";

            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var statusEl) || statusEl.GetInt32() != 200)
                return null;
            if (!root.TryGetProperty("data", out var dataEl) || dataEl.GetArrayLength() == 0)
                return null;

            var last = dataEl.EnumerateArray().Last();
            foreach (var field in new[] { "vix", "close", "TWVIX", "value", "VIX" })
            {
                if (last.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.Number)
                    return Math.Round(el.GetDouble(), 2);
            }
            return null;
        }
        catch { return null; }
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
