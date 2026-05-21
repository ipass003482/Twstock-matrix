using System.Text.Json;

namespace StockMatrix.Services;

/// <summary>
/// 計算外資累積平均成本（FinLab 風格）：
/// 以每日 (open+high+low+close)/4 為當日均價，
/// 走訪近 N 天「外資買賣超」與 K 線：
///   - 外資淨買 (net &gt; 0)：成本 = (持股*成本 + net*均價) / (持股+net)
///   - 外資淨賣 (net &lt; 0)：持股遞減，成本不變；持股 &lt;= 0 時部位歸零、成本歸零（重置）
/// 結果近似為「過去視窗內外資的平均建倉成本」。
/// </summary>
public class ForeignCostService
{
    private readonly HttpClient _http;
    private readonly ILogger<ForeignCostService> _logger;
    private const string BaseUrl = "https://api.finmindtrade.com/api/v4/data";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private (DateTime fetchedAt, List<Item> items)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // 預設追蹤的 4 檔
    public static readonly (string Code, string Name)[] DefaultTickers =
    {
        ("2330", "台積電"),
        ("2317", "鴻海"),
        ("2454", "聯發科"),
        ("3356", "奇偶"),
        ("1303", "南亞"),
    };

    public ForeignCostService(HttpClient http, ILogger<ForeignCostService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public record Item(
        string Code,
        string Name,
        double AvgCost,
        double LastClose,
        double DiffPct,
        long Position,
        string AsOf,
        int Days,
        string? Error = null);

    public record Snapshot(string UpdatedAt, IReadOnlyList<Item> Items);

    public async Task<Snapshot> GetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cache.HasValue && DateTime.UtcNow - _cache.Value.fetchedAt < CacheTtl)
                return new Snapshot(_cache.Value.fetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), _cache.Value.items);

            var tasks = DefaultTickers.Select(t => ComputeOneAsync(t.Code, t.Name)).ToArray();
            var items = (await Task.WhenAll(tasks)).ToList();
            _cache = (DateTime.UtcNow, items);
            return new Snapshot(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC", items);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Item> ComputeOneAsync(string code, string name)
    {
        try
        {
            var end   = DateTime.Today;
            var start = end.AddDays(-365);
            var startStr = start.ToString("yyyy-MM-dd");
            var endStr   = end.ToString("yyyy-MM-dd");

            var priceTask = FetchPriceAsync(code, startStr, endStr);
            var instTask  = FetchForeignNetAsync(code, startStr, endStr);
            await Task.WhenAll(priceTask, instTask);

            var prices = await priceTask;       // date -> avg price
            var nets   = await instTask;        // date -> net (shares)

            if (prices.Count == 0)
                return new Item(code, name, 0, 0, 0, 0, "", 0, "找不到 K 線");
            if (nets.Count == 0)
                return new Item(code, name, 0, 0, 0, 0, "", 0, "找不到外資資料");

            // 依日期合併
            var dates = prices.Keys.Intersect(nets.Keys).OrderBy(d => d).ToList();
            double position = 0, cost = 0;
            // fallback：所有「淨買日」之加權平均價
            double sumBuyShares = 0, sumBuyValue = 0;
            foreach (var d in dates)
            {
                var net = nets[d];
                var px  = prices[d];
                if (net > 0)
                {
                    var newPos = position + net;
                    cost = (position * cost + net * px) / newPos;
                    position = newPos;
                    sumBuyShares += net;
                    sumBuyValue  += net * px;
                }
                else if (net < 0)
                {
                    position += net;     // net 為負 -> 減持
                    if (position <= 0)
                    {
                        position = 0;
                        cost = 0;
                    }
                }
            }

            // 若執行成本歸零（外資長期淨賣 > 淨買），改用淨買加權平均
            if (cost <= 0 && sumBuyShares > 0)
                cost = sumBuyValue / sumBuyShares;

            // 找最新收盤
            var lastDate = prices.Keys.Max();
            var lastClose = prices[lastDate];   // 用同一個 avg；下面再用真正的 close 覆寫
            // 不過我們也想顯示「真正的最新收盤」，下面 FetchPriceAsync 也順便存收盤
            var lastCloseReal = _lastClosesCache.TryGetValue(code, out var lc) ? lc : lastClose;

            var diffPct = (cost > 0) ? (lastCloseReal - cost) / cost * 100.0 : 0;
            return new Item(
                Code: code,
                Name: name,
                AvgCost: Math.Round(cost, 2),
                LastClose: Math.Round(lastCloseReal, 2),
                DiffPct: Math.Round(diffPct, 2),
                Position: (long)position,
                AsOf: lastDate.ToString("yyyy-MM-dd"),
                Days: dates.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ForeignCost compute failed for {Code}", code);
            return new Item(code, name, 0, 0, 0, 0, "", 0, ex.Message);
        }
    }

    // 暫存每檔最新收盤
    private readonly Dictionary<string, double> _lastClosesCache = new();

    private async Task<Dictionary<DateTime, double>> FetchPriceAsync(string code, string start, string end)
    {
        var url = $"{BaseUrl}?dataset=TaiwanStockPrice&data_id={code}&start_date={start}&end_date={end}";
        using var resp = await _http.GetAsync(FinmindAuth.Append(url));
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var dict = new Dictionary<DateTime, double>();
        if (!doc.RootElement.TryGetProperty("data", out var arr)) return dict;

        DateTime? lastDate = null;
        double lastClose = 0;
        foreach (var it in arr.EnumerateArray())
        {
            var date  = DateTime.Parse(it.GetProperty("date").GetString()!);
            var open  = it.GetProperty("open").GetDouble();
            var high  = it.GetProperty("max").GetDouble();
            var low   = it.GetProperty("min").GetDouble();
            var close = it.GetProperty("close").GetDouble();
            if (close <= 0) continue;
            var avg = (open + high + low + close) / 4.0;
            dict[date] = avg;
            if (lastDate == null || date > lastDate)
            {
                lastDate = date;
                lastClose = close;
            }
        }
        if (lastDate != null) _lastClosesCache[code] = lastClose;
        return dict;
    }

    private async Task<Dictionary<DateTime, long>> FetchForeignNetAsync(string code, string start, string end)
    {
        var url = $"{BaseUrl}?dataset=TaiwanStockInstitutionalInvestorsBuySell&data_id={code}&start_date={start}&end_date={end}";
        using var resp = await _http.GetAsync(FinmindAuth.Append(url));
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var dict = new Dictionary<DateTime, long>();
        if (!doc.RootElement.TryGetProperty("data", out var arr)) return dict;

        foreach (var it in arr.EnumerateArray())
        {
            var nm = it.GetProperty("name").GetString();
            if (nm != "Foreign_Investor") continue;
            var date = DateTime.Parse(it.GetProperty("date").GetString()!);
            var buy  = it.GetProperty("buy").GetInt64();
            var sell = it.GetProperty("sell").GetInt64();
            var net  = buy - sell;
            // 同一日如有重複 row 累加（罕見）
            if (dict.TryGetValue(date, out var prev)) dict[date] = prev + net;
            else dict[date] = net;
        }
        return dict;
    }
}
