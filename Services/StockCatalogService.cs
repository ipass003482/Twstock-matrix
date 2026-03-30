using System.Text.Json;
using StockMatrix.Models;

namespace StockMatrix.Services;

public class StockCatalogService
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _codeToName = [];
    private readonly Dictionary<string, string> _nameToCode = [];
    private readonly List<StockSearchItem> _list = [];
    private bool _loaded;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public StockCatalogService(IHttpClientFactory factory)
    {
        _http = factory.CreateClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _lock.WaitAsync();
        try
        {
            if (_loaded) return;
            await LoadTwseAsync();
            await LoadTpexAsync();
            _loaded = true;
        }
        finally { _lock.Release(); }
    }

    private async Task LoadTwseAsync()
    {
        try
        {
            var url = "https://openapi.twse.com.tw/v1/opendata/t187ap03_L";
            using var stream = await _http.GetStreamAsync(url);
            using var doc    = await JsonDocument.ParseAsync(stream);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var code  = item.TryGetString("公司代號");
                var sname = item.TryGetString("公司簡稱");
                var fname = item.TryGetString("公司名稱");
                if (string.IsNullOrWhiteSpace(code)) continue;
                var name = sname ?? fname ?? code;
                Register(code.Trim(), name.Trim(), fname?.Trim());
            }
        }
        catch { /* 靜默失敗，仍可用代號查詢 */ }
    }

    private async Task LoadTpexAsync()
    {
        try
        {
            var url = "https://www.tpex.org.tw/openapi/v1/tpex_mainboard_quotes";
            using var stream = await _http.GetStreamAsync(url);
            using var doc    = await JsonDocument.ParseAsync(stream);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var code = item.TryGetString("SecuritiesCompanyCode")
                        ?? item.TryGetString("股票代號");
                var name = item.TryGetString("CompanyName")
                        ?? item.TryGetString("公司簡稱");
                if (string.IsNullOrWhiteSpace(code)) continue;
                if (!_codeToName.ContainsKey(code.Trim()))
                    Register(code.Trim(), name?.Trim() ?? code.Trim(), null);
            }
        }
        catch { }
    }

    private void Register(string code, string shortName, string? fullName)
    {
        _codeToName[code] = shortName;
        _list.Add(new StockSearchItem(code, shortName));
        _nameToCode[shortName] = code;
        if (fullName != null && fullName != shortName)
            _nameToCode[fullName] = code;
    }

    public string GetName(string code) =>
        _codeToName.TryGetValue(code, out var n) ? n : code;

    public string ResolveToCode(string query)
    {
        if (_codeToName.ContainsKey(query)) return query;
        if (_nameToCode.TryGetValue(query, out var exact)) return exact;
        // 部分符合
        foreach (var kv in _nameToCode)
            if (kv.Key.Contains(query))
                return kv.Value;
        return query;
    }

    public IEnumerable<StockSearchItem> Search(string q, int limit = 10) =>
        _list.Where(x => x.Code.Contains(q) || x.Name.Contains(q)).Take(limit);
}

internal static class JsonElementExtensions
{
    public static string? TryGetString(this JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
