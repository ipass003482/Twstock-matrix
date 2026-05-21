using System.Text.Json;

namespace StockMatrix.Services;

/// <summary>
/// 抓 FinMind TaiwanFuturesInstitutionalInvestors（台指期 TX 三大法人未平倉）
/// 回傳「外資」最新一日的多/空當日成交口數、未平倉淨多空、與前一日的變化。
/// </summary>
public class FuturesInstitutionalService
{
    private readonly HttpClient _http;
    private readonly ILogger<FuturesInstitutionalService> _logger;
    private const string BaseUrl = "https://api.finmindtrade.com/api/v4/data";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private (DateTime fetchedAt, Snapshot snap)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FuturesInstitutionalService(HttpClient http, ILogger<FuturesInstitutionalService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public record Snapshot(
        string AsOf,
        long LongDealVolume,
        long ShortDealVolume,
        long DealNet,
        long LongOi,
        long ShortOi,
        long NetOi,
        long? NetOiChange,
        string? Error = null);

    public async Task<Snapshot> GetForeignTxAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cache.HasValue && DateTime.UtcNow - _cache.Value.fetchedAt < CacheTtl)
                return _cache.Value.snap;

            var end = DateTime.Today;
            var start = end.AddDays(-10);
            var url = $"{BaseUrl}?dataset=TaiwanFuturesInstitutionalInvestors&data_id=TX&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}";

            try
            {
                using var resp = await _http.GetAsync(FinmindAuth.Append(url));
                resp.EnsureSuccessStatusCode();
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                if (!doc.RootElement.TryGetProperty("data", out var arr))
                    return Err("FinMind 無 data");

                var foreignRows = new List<(DateTime date, long longDeal, long shortDeal, long longOi, long shortOi)>();
                foreach (var it in arr.EnumerateArray())
                {
                    var who = it.GetProperty("institutional_investors").GetString();
                    if (who != "外資" && who != "Foreign_Investor") continue;
                    var date = DateTime.Parse(it.GetProperty("date").GetString()!);
                    foreignRows.Add((
                        date,
                        it.GetProperty("long_deal_volume").GetInt64(),
                        it.GetProperty("short_deal_volume").GetInt64(),
                        it.GetProperty("long_open_interest_balance_volume").GetInt64(),
                        it.GetProperty("short_open_interest_balance_volume").GetInt64()));
                }

                if (foreignRows.Count == 0)
                    return Err("找不到外資資料");

                foreignRows.Sort((a, b) => a.date.CompareTo(b.date));
                var last = foreignRows[^1];
                long? prevNetOi = foreignRows.Count >= 2
                    ? foreignRows[^2].longOi - foreignRows[^2].shortOi
                    : null;
                var netOi = last.longOi - last.shortOi;
                var snap = new Snapshot(
                    AsOf: last.date.ToString("yyyy-MM-dd"),
                    LongDealVolume: last.longDeal,
                    ShortDealVolume: last.shortDeal,
                    DealNet: last.longDeal - last.shortDeal,
                    LongOi: last.longOi,
                    ShortOi: last.shortOi,
                    NetOi: netOi,
                    NetOiChange: prevNetOi.HasValue ? netOi - prevNetOi.Value : null);
                _cache = (DateTime.UtcNow, snap);
                return snap;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FuturesInstitutional fetch failed");
                return Err(ex.Message);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Snapshot Err(string msg) =>
        new("", 0, 0, 0, 0, 0, 0, null, msg);
}
