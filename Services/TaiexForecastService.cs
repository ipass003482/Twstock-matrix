using System.Text.Json;

namespace StockMatrix.Services;

/// <summary>
/// 啟發式（非機器學習）的明日 TAIEX 漲跌方向估計：
/// 用三個訊號各投一票，並對過去 ~250 個交易日做樣本內回測，回報命中率。
/// 訊號：
///   1) 基差變化方向：(TX近月收盤 − TAIEX收盤) 較前日的差。
///   2) 外資 TX 未平倉淨多空變化方向。
///   3) 近 5 日 TAIEX 動能（5 日報酬正負）。
/// </summary>
public class TaiexForecastService
{
    private readonly HttpClient _http;
    private readonly ILogger<TaiexForecastService> _logger;
    private const string BaseUrl = "https://api.finmindtrade.com/api/v4/data";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(2);

    private (DateTime fetchedAt, Snapshot snap)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TaiexForecastService(HttpClient http, ILogger<TaiexForecastService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public record SignalDetail(string Name, int Vote, string Reason);

    public record Snapshot(
        string AsOf,
        int Score,
        int MaxScore,
        double UpProbability,
        string Direction,
        IReadOnlyList<SignalDetail> Signals,
        int BacktestDays,
        int BacktestHits,
        double BacktestHitRate,
        string? Error = null);

    public async Task<Snapshot> GetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cache.HasValue && DateTime.UtcNow - _cache.Value.fetchedAt < CacheTtl)
                return _cache.Value.snap;

            var snap = await BuildAsync();
            _cache = (DateTime.UtcNow, snap);
            return snap;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Snapshot> BuildAsync()
    {
        var end = DateTime.Today;
        var start = end.AddDays(-420); // 約 1.2 年自然日 → ~250 交易日

        try
        {
            var taiexTask = FetchTaiexAsync(start, end);
            var txTask = FetchTxNearAsync(start, end);
            var oiTask = FetchForeignOiAsync(start, end);
            await Task.WhenAll(taiexTask, txTask, oiTask);

            var taiex = taiexTask.Result; // date -> close
            var tx = txTask.Result;       // date -> close（近月、一般盤）
            var oi = oiTask.Result;       // date -> long_oi - short_oi

            var dates = taiex.Keys.Where(d => tx.ContainsKey(d) && oi.ContainsKey(d))
                                  .OrderBy(d => d).ToList();
            if (dates.Count < 30)
                return Err("資料不足");

            // 預先計算每日特徵
            var basis = new Dictionary<DateTime, double>();
            foreach (var d in dates) basis[d] = tx[d] - taiex[d];

            var feats = new List<(DateTime date, int v1, int v2, int v3)>();
            for (int i = 5; i < dates.Count; i++)
            {
                var d = dates[i];
                var dPrev = dates[i - 1];
                var dM5 = dates[i - 5];

                int v1 = Math.Sign(basis[d] - basis[dPrev]);
                int v2 = Math.Sign(oi[d] - oi[dPrev]);
                double ret5 = taiex[d] / taiex[dM5] - 1.0;
                int v3 = Math.Sign(ret5);
                feats.Add((d, v1, v2, v3));
            }

            // 回測：用第 i 天的訊號預測第 i+1 天 TAIEX 方向
            int hits = 0, total = 0;
            for (int i = 0; i < feats.Count - 1; i++)
            {
                var f = feats[i];
                var dToday = f.date;
                var dNext = feats[i + 1].date;
                int score = f.v1 + f.v2 + f.v3;
                if (score == 0) continue; // 中性票數不參與命中率
                int actual = Math.Sign(taiex[dNext] - taiex[dToday]);
                if (actual == 0) continue;
                if (Math.Sign(score) == actual) hits++;
                total++;
            }

            // 取最後一日訊號 → 對「明日」預測
            var last = feats[^1];
            int latestScore = last.v1 + last.v2 + last.v3;

            string ReasonV1(int v) => v > 0 ? $"基差較前日上升 ({basis[last.date] - basis[dates[dates.IndexOf(last.date) - 1]]:+0.00;-0.00})"
                                : v < 0 ? $"基差較前日下降 ({basis[last.date] - basis[dates[dates.IndexOf(last.date) - 1]]:+0.00;-0.00})"
                                : "基差持平";
            string ReasonV2(int v)
            {
                long delta = (long)Math.Round(oi[last.date] - oi[dates[dates.IndexOf(last.date) - 1]]);
                return v > 0 ? $"外資淨多空增加 ({delta:+#,0;-#,0;0})"
                     : v < 0 ? $"外資淨多空減少 ({delta:+#,0;-#,0;0})"
                     : "外資淨多空持平";
            }
            string ReasonV3(int v)
            {
                int idx = dates.IndexOf(last.date);
                double ret5 = taiex[last.date] / taiex[dates[idx - 5]] - 1.0;
                return v > 0 ? $"近 5 日動能 +{ret5 * 100:0.00}%"
                     : v < 0 ? $"近 5 日動能 {ret5 * 100:0.00}%"
                     : "近 5 日動能 0%";
            }

            var signals = new List<SignalDetail>
            {
                new("TX 基差變化", last.v1, ReasonV1(last.v1)),
                new("外資 TX 未平倉淨多空變化", last.v2, ReasonV2(last.v2)),
                new("TAIEX 5 日動能", last.v3, ReasonV3(last.v3)),
            };

            double hitRate = total > 0 ? (double)hits / total : 0.0;
            // 將分數轉成「看多機率」：用回測命中率作為信心錨點，避免假裝精確
            // score = +3/+1/-1/-3，正分→使用 hitRate；負分→使用 1-hitRate；0→0.5
            double upProb;
            if (latestScore > 0) upProb = hitRate;
            else if (latestScore < 0) upProb = 1.0 - hitRate;
            else upProb = 0.5;

            string dir = latestScore > 0 ? "偏多"
                       : latestScore < 0 ? "偏空"
                       : "中性";

            return new Snapshot(
                AsOf: last.date.ToString("yyyy-MM-dd"),
                Score: latestScore,
                MaxScore: 3,
                UpProbability: Math.Round(upProb, 3),
                Direction: dir,
                Signals: signals,
                BacktestDays: total,
                BacktestHits: hits,
                BacktestHitRate: Math.Round(hitRate, 3));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TaiexForecast build failed");
            return Err(ex.Message);
        }
    }

    private async Task<Dictionary<DateTime, double>> FetchTaiexAsync(DateTime start, DateTime end)
    {
        var url = $"{BaseUrl}?dataset=TaiwanStockPrice&data_id=TAIEX&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var dict = new Dictionary<DateTime, double>();
        if (!doc.RootElement.TryGetProperty("data", out var arr)) return dict;
        foreach (var it in arr.EnumerateArray())
        {
            var dateStr = it.GetProperty("date").GetString();
            if (string.IsNullOrEmpty(dateStr)) continue;
            if (!it.TryGetProperty("close", out var cl) || cl.ValueKind != JsonValueKind.Number) continue;
            var c = cl.GetDouble();
            if (c <= 0) continue;
            dict[DateTime.Parse(dateStr)] = c;
        }
        return dict;
    }

    private async Task<Dictionary<DateTime, double>> FetchTxNearAsync(DateTime start, DateTime end)
    {
        var url = $"{BaseUrl}?dataset=TaiwanFuturesDaily&data_id=TX&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var dict = new Dictionary<DateTime, double>();
        if (!doc.RootElement.TryGetProperty("data", out var arr)) return dict;
        // 每日近月（一般盤優先）
        var rows = new List<(DateTime date, string contract, double close, string session)>();
        foreach (var it in arr.EnumerateArray())
        {
            var dateStr = it.GetProperty("date").GetString();
            if (string.IsNullOrEmpty(dateStr)) continue;
            if (!it.TryGetProperty("contract_date", out var cd)) continue;
            string contract = cd.GetString() ?? "";
            if (string.IsNullOrEmpty(contract)) continue;
            if (!it.TryGetProperty("close", out var cl) || cl.ValueKind != JsonValueKind.Number) continue;
            var c = cl.GetDouble();
            if (c <= 0) continue;
            string session = it.TryGetProperty("trading_session", out var ts) ? (ts.GetString() ?? "") : "";
            rows.Add((DateTime.Parse(dateStr), contract, c, session));
        }
        foreach (var grp in rows.GroupBy(r => r.date))
        {
            var pos = grp.Where(r => r.session == "position").ToList();
            var pool = pos.Count > 0 ? pos : grp.ToList();
            var near = pool.OrderBy(r => r.contract).First();
            dict[grp.Key] = near.close;
        }
        return dict;
    }

    private async Task<Dictionary<DateTime, double>> FetchForeignOiAsync(DateTime start, DateTime end)
    {
        var url = $"{BaseUrl}?dataset=TaiwanFuturesInstitutionalInvestors&data_id=TX&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var dict = new Dictionary<DateTime, double>();
        if (!doc.RootElement.TryGetProperty("data", out var arr)) return dict;
        foreach (var it in arr.EnumerateArray())
        {
            var who = it.GetProperty("institutional_investors").GetString();
            if (who != "外資" && who != "Foreign_Investor") continue;
            var dateStr = it.GetProperty("date").GetString();
            if (string.IsNullOrEmpty(dateStr)) continue;
            long lo = it.GetProperty("long_open_interest_balance_volume").GetInt64();
            long so = it.GetProperty("short_open_interest_balance_volume").GetInt64();
            dict[DateTime.Parse(dateStr)] = lo - so;
        }
        return dict;
    }

    private static Snapshot Err(string msg) =>
        new("", 0, 3, 0.5, "未知", Array.Empty<SignalDetail>(), 0, 0, 0, msg);
}
