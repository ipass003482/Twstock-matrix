using System.Text.Json;

namespace StockMatrix.Services;

/// <summary>
/// 抓 FinMind 加權指數 (TAIEX) 與 台指期近月 (TX) 收盤價與漲跌。
/// </summary>
public class MarketIndexService
{
    private readonly HttpClient _http;
    private readonly ILogger<MarketIndexService> _logger;
    private const string BaseUrl = "https://api.finmindtrade.com/api/v4/data";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private (DateTime fetchedAt, Snapshot snap)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MarketIndexService(HttpClient http, ILogger<MarketIndexService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public record IndexInfo(string AsOf, double Close, double Change, double ChangePct, string? Extra = null);

    public record Snapshot(IndexInfo? Taiex, IndexInfo? TxNear, string? Error = null);

    public async Task<Snapshot> GetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cache.HasValue && DateTime.UtcNow - _cache.Value.fetchedAt < CacheTtl)
                return _cache.Value.snap;

            var end = DateTime.Today;
            var start = end.AddDays(-10);

            IndexInfo? taiex = null;
            IndexInfo? tx = null;
            string? err = null;

            try
            {
                taiex = await FetchTaiexAsync(start, end);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MarketIndex TAIEX failed");
                err = "TAIEX: " + ex.Message;
            }

            try
            {
                tx = await FetchTxNearAsync(start, end);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MarketIndex TX failed");
                err = (err == null ? "" : err + "; ") + "TX: " + ex.Message;
            }

            var snap = new Snapshot(taiex, tx, err);
            _cache = (DateTime.UtcNow, snap);
            return snap;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IndexInfo?> FetchTaiexAsync(DateTime start, DateTime end)
    {
        var url = $"{BaseUrl}?dataset=TaiwanStockPrice&data_id=TAIEX&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        if (!doc.RootElement.TryGetProperty("data", out var arr)) return null;

        var rows = new List<(DateTime date, double close)>();
        foreach (var it in arr.EnumerateArray())
        {
            var dateStr = it.GetProperty("date").GetString();
            if (string.IsNullOrEmpty(dateStr)) continue;
            if (!it.TryGetProperty("close", out var closeEl)) continue;
            if (closeEl.ValueKind != JsonValueKind.Number) continue;
            rows.Add((DateTime.Parse(dateStr), closeEl.GetDouble()));
        }
        if (rows.Count == 0) return null;
        rows.Sort((a, b) => a.date.CompareTo(b.date));
        var last = rows[^1];
        var prev = rows.Count >= 2 ? rows[^2].close : last.close;
        var change = last.close - prev;
        var pct = prev != 0 ? change / prev * 100 : 0;
        return new IndexInfo(last.date.ToString("yyyy-MM-dd"), last.close, Math.Round(change, 2), Math.Round(pct, 2));
    }

    private async Task<IndexInfo?> FetchTxNearAsync(DateTime start, DateTime end)
    {
        var url = $"{BaseUrl}?dataset=TaiwanFuturesDaily&data_id=TX&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        if (!doc.RootElement.TryGetProperty("data", out var arr)) return null;

        // rows: (date, contract_date, close, trading_session)
        var rows = new List<(DateTime date, string contract, double close, string session)>();
        foreach (var it in arr.EnumerateArray())
        {
            var dateStr = it.GetProperty("date").GetString();
            if (string.IsNullOrEmpty(dateStr)) continue;
            string contract = it.TryGetProperty("contract_date", out var cd) ? (cd.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(contract)) continue;
            if (!it.TryGetProperty("close", out var closeEl)) continue;
            if (closeEl.ValueKind != JsonValueKind.Number) continue;
            var close = closeEl.GetDouble();
            if (close <= 0) continue;
            string session = it.TryGetProperty("trading_session", out var ts) ? (ts.GetString() ?? "") : "";
            rows.Add((DateTime.Parse(dateStr), contract, close, session));
        }
        if (rows.Count == 0) return null;

        // 取每日的近月（最小 contract_date），優先 trading_session=="position"（一般盤）
        var perDay = rows
            .GroupBy(r => r.date)
            .Select(g =>
            {
                var preferred = g.Where(r => r.session == "position").ToList();
                var pool = preferred.Count > 0 ? preferred : g.ToList();
                var near = pool.OrderBy(r => r.contract).First();
                return (date: g.Key, contract: near.contract, close: near.close);
            })
            .OrderBy(x => x.date)
            .ToList();

        if (perDay.Count == 0) return null;
        var last = perDay[^1];
        var prev = perDay.Count >= 2 ? perDay[^2].close : last.close;
        var change = last.close - prev;
        var pct = prev != 0 ? change / prev * 100 : 0;
        return new IndexInfo(last.date.ToString("yyyy-MM-dd"), last.close, Math.Round(change, 2), Math.Round(pct, 2), Extra: last.contract);
    }
}
