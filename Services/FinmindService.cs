using System.Text.Json;
using System.Text.Json.Serialization;
using StockMatrix.Models;

namespace StockMatrix.Services;

public class FinmindService
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://api.finmindtrade.com/api/v4/data";

    public FinmindService(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<StockBar>> GetHistoryAsync(string stockId)
    {
        var start = DateTime.Today.AddDays(-730).ToString("yyyy-MM-dd");
        var end   = DateTime.Today.ToString("yyyy-MM-dd");
        var url   = $"{BaseUrl}?dataset=TaiwanStockPrice&data_id={stockId}&start_date={start}&end_date={end}";

        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = doc.RootElement;

        if (!root.TryGetProperty("status", out var statusEl) || statusEl.GetInt32() != 200)
            return [];

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.GetArrayLength() == 0)
            return [];

        var bars = new List<StockBar>();
        foreach (var item in dataEl.EnumerateArray())
        {
            var date   = DateTime.Parse(item.GetProperty("date").GetString()!);
            var open   = item.GetProperty("open").GetDouble();
            var high   = item.GetProperty("max").GetDouble();
            var low    = item.GetProperty("min").GetDouble();
            var close  = item.GetProperty("close").GetDouble();
            var volume = item.GetProperty("Trading_Volume").GetInt64();
            bars.Add(new StockBar(date, open, high, low, close, volume));
        }

        return [.. bars.OrderBy(b => b.Date)];
    }
}
