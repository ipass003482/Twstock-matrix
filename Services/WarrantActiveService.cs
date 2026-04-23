using System.Text.Json;
using StockMatrix.Models;

namespace StockMatrix.Services;

/// <summary>
/// 抓 TWSE 每日認購權證行情 (MI_INDEX type=0999)，依成交股數排序回傳 Top N。
/// 資料每天盤後更新，內部快取 30 分鐘；遇假日/查無資料自動往前 1~3 天回退。
/// </summary>
public class WarrantActiveService
{
    private readonly HttpClient _http;
    private readonly ILogger<WarrantActiveService> _logger;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private (DateTime asOf, List<WarrantQuote> list, DateTime fetchedAt)? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WarrantActiveService(HttpClient http, ILogger<WarrantActiveService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public record Snapshot(string AsOf, int Total, IReadOnlyList<WarrantQuote> Items);

    public async Task<Snapshot> GetTopAsync(int top = 50)
    {
        await _lock.WaitAsync();
        try
        {
            if (_cache.HasValue && DateTime.UtcNow - _cache.Value.fetchedAt < CacheTtl)
            {
                var c = _cache.Value;
                return new Snapshot(c.asOf.ToString("yyyy-MM-dd"), c.list.Count, c.list.Take(top).ToList());
            }

            // 嘗試今日 → 往前 3 天 (假日/收盤前)
            var taipeiNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                GetTaipeiTz());
            for (int back = 0; back <= 3; back++)
            {
                var date = taipeiNow.AddDays(-back).Date;
                var list = await FetchAsync(date);
                if (list.Count > 0)
                {
                    _cache = (date, list, DateTime.UtcNow);
                    return new Snapshot(date.ToString("yyyy-MM-dd"), list.Count, list.Take(top).ToList());
                }
            }

            return new Snapshot(taipeiNow.ToString("yyyy-MM-dd"), 0, Array.Empty<WarrantQuote>());
        }
        finally
        {
            _lock.Release();
        }
    }

    private static TimeZoneInfo GetTaipeiTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"); }
    }

    private async Task<List<WarrantQuote>> FetchAsync(DateTime date)
    {
        var url = $"https://www.twse.com.tw/exchangeReport/MI_INDEX?type=0999&response=json&date={date:yyyyMMdd}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 StockMatrix/1.0");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("TWSE warrants HTTP {Status} for {Date}", resp.StatusCode, date);
                return new();
            }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("stat", out var stEl) || stEl.GetString() != "OK")
                return new();
            if (!root.TryGetProperty("tables", out var tablesEl))
                return new();

            // 找出含 "認購權證" 標題且 fields 含 "成交股數" 的表
            JsonElement? warrantTable = null;
            foreach (var t in tablesEl.EnumerateArray())
            {
                if (!t.TryGetProperty("title", out var tt)) continue;
                var title = tt.GetString() ?? string.Empty;
                if (!title.Contains("認購權證")) continue;
                if (!t.TryGetProperty("data", out var dataEl) || dataEl.GetArrayLength() == 0) continue;
                warrantTable = t;
                break;
            }
            if (warrantTable == null) return new();

            // 解析欄位索引（防 TWSE 改順序）
            var fields = warrantTable.Value.GetProperty("fields").EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty).ToArray();
            int IdxOf(string name) => Array.FindIndex(fields, f => f.Contains(name));
            int iCode  = IdxOf("證券代號");
            int iName  = IdxOf("證券名稱");
            int iVol   = IdxOf("成交股數");
            int iCnt   = IdxOf("成交筆數");
            int iVal   = IdxOf("成交金額");
            int iClose = IdxOf("收盤價");
            int iSign  = IdxOf("漲跌(+/-)");
            int iDiff  = IdxOf("漲跌價差");
            int iUCode = IdxOf("標的代號");
            int iUName = IdxOf("標的名稱");
            int iUClose= IdxOf("標的收盤價");
            if (iCode < 0 || iVol < 0) return new();

            var list = new List<WarrantQuote>(2048);
            foreach (var row in warrantTable.Value.GetProperty("data").EnumerateArray())
            {
                try
                {
                    string s(int i) => i < 0 ? "" : (row[i].GetString() ?? "").Trim();
                    long  l(int i) => long.TryParse(s(i).Replace(",", ""), out var v) ? v : 0;
                    double d(int i) => double.TryParse(s(i).Replace(",", ""), out var v) ? v : 0;

                    var vol = l(iVol);
                    if (vol <= 0) continue;

                    // TWSE 漲跌欄常包成 <p style=color:red>+</p> / <p style=color:green>-</p>
                    var rawSign = s(iSign);
                    string sign = "";
                    if (rawSign.Contains('+')) sign = "+";
                    else if (rawSign.Contains('-') || rawSign.Contains('−')) sign = "-";

                    list.Add(new WarrantQuote(
                        Code: s(iCode),
                        Name: s(iName),
                        Volume: vol,
                        TradeCount: l(iCnt),
                        TradeValue: l(iVal),
                        Close: d(iClose),
                        Change: d(iDiff),
                        ChangeSign: sign,
                        UnderlyingCode: s(iUCode),
                        UnderlyingName: s(iUName),
                        UnderlyingClose: d(iUClose)
                    ));
                }
                catch { /* 跳過異常列 */ }
            }

            list.Sort((a, b) => b.Volume.CompareTo(a.Volume));
            _logger.LogInformation("Warrant active: {Date} parsed {Count}", date, list.Count);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Warrant active fetch failed for {Date}", date);
            return new();
        }
    }
}
