using System.Text.Json;

namespace StockMatrix.Services;

/// <summary>
/// 取得美股隔夜行情（DJI / NASDAQ / S&P500 / VIX）並推算對台股開盤的方向訊號。
/// 來源：Yahoo Finance chart API（免費）。
/// </summary>
public class UsMarketService
{
    private readonly HttpClient _http;
    private readonly ILogger<UsMarketService> _logger;

    private static UsOvernightSnapshot? _cache;
    private static DateTime _cacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    public UsMarketService(HttpClient http, ILogger<UsMarketService> logger)
    {
        _http = http;
        _logger = logger;
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public record IndexQuote(double? Price, double? PrevClose, double? ChangePct);
    public record UsOvernightSnapshot(
        IndexQuote Dji,
        IndexQuote Nasdaq,
        IndexQuote Sp500,
        IndexQuote Vix,
        string Signal,        // bullish / bearish / neutral
        string SignalLabel,
        double SignalScore,   // -1 ~ +1
        string UpdatedAt);

    public async Task<UsOvernightSnapshot> GetAsync()
    {
        if (_cache != null && DateTime.UtcNow - _cacheTime < CacheTtl)
            return _cache;

        var dji = await FetchAsync("%5EDJI");
        var ndx = await FetchAsync("%5ENDX");
        var spx = await FetchAsync("%5EGSPC");
        var vix = await FetchAsync("%5EVIX");

        // 加權方向分數：
        // SP500 權重 0.5, NASDAQ 0.3, DJI 0.2；VIX 反向 (VIX 漲 = 偏空)
        double score = 0;
        int n = 0;
        if (spx.ChangePct.HasValue) { score += 0.5 * Math.Sign(spx.ChangePct.Value) * Math.Min(Math.Abs(spx.ChangePct.Value), 3); n++; }
        if (ndx.ChangePct.HasValue) { score += 0.3 * Math.Sign(ndx.ChangePct.Value) * Math.Min(Math.Abs(ndx.ChangePct.Value), 3); n++; }
        if (dji.ChangePct.HasValue) { score += 0.2 * Math.Sign(dji.ChangePct.Value) * Math.Min(Math.Abs(dji.ChangePct.Value), 3); n++; }
        if (vix.ChangePct.HasValue) { score += -0.15 * Math.Sign(vix.ChangePct.Value) * Math.Min(Math.Abs(vix.ChangePct.Value) / 5, 1); }

        // 正規化到 -1 ~ +1
        score = Math.Max(-1, Math.Min(1, score / 2.0));

        string signal, label;
        if (score > 0.25) { signal = "bullish"; label = "偏多"; }
        else if (score < -0.25) { signal = "bearish"; label = "偏空"; }
        else { signal = "neutral"; label = "中性"; }

        _cache = new UsOvernightSnapshot(dji, ndx, spx, vix, signal, label, Math.Round(score, 3),
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC");
        _cacheTime = DateTime.UtcNow;
        return _cache;
    }

    private async Task<IndexQuote> FetchAsync(string symbol)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?range=5d&interval=1d";
            var stream = await _http.GetStreamAsync(url, cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
            double price = meta.GetProperty("regularMarketPrice").GetDouble();
            double prev  = meta.GetProperty("chartPreviousClose").GetDouble();
            double pct   = prev > 0 ? (price - prev) / prev * 100 : 0;
            return new IndexQuote(Math.Round(price, 2), Math.Round(prev, 2), Math.Round(pct, 2));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("US fetch failed for {Symbol}: {Msg}", symbol, ex.Message);
            return new IndexQuote(null, null, null);
        }
    }
}
