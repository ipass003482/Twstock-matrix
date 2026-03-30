using System.Text.Json;
using System.Text.Json.Serialization;
using StockMatrix.Models;
using StockMatrix.Services;

var builder = WebApplication.CreateBuilder(args);

// Railway 等平台透過 PORT 環境變數指定監聽 Port
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<FinmindService>();
builder.Services.AddSingleton<StockCatalogService>();
builder.Services.AddSingleton<DecisionMatrixService>();

var app = builder.Build();
app.UseStaticFiles();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
};

// ── GET /api/search?q=... ────────────────────────────────
app.MapGet("/api/search", async (string? q, StockCatalogService catalog) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.Json(Array.Empty<object>(), jsonOpts);
    await catalog.EnsureLoadedAsync();
    var results = catalog.Search(q, 10);
    return Results.Json(results, jsonOpts);
});

// ── GET /api/analyze/{ticker} ────────────────────────────
app.MapGet("/api/analyze/{ticker}", async (
    string ticker,
    FinmindService finmind,
    StockCatalogService catalog,
    DecisionMatrixService matrix) =>
{
    await catalog.EnsureLoadedAsync();
    var stockId = catalog.ResolveToCode(ticker.Trim());

    List<StockBar> bars;
    try
    {
        bars = await finmind.GetHistoryAsync(stockId);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"資料取得失敗：{ex.Message}" }, jsonOpts, statusCode: 500);
    }

    if (bars.Count < 60)
        return Results.Json(
            new { error = $"找不到股票代號 {stockId} 的資料，或歷史資料不足 60 筆。請確認代號是否正確（例如：2330、0050）。" },
            jsonOpts, statusCode: 404);

    MarketState state;
    try { state = matrix.AnalyzeState(bars); }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"指標計算失敗：{ex.Message}" }, jsonOpts, statusCode: 500);
    }

    var decision = matrix.GetDecision(state.TrendState, state.VolState);
    var name     = catalog.GetName(stockId);

    // 最近 180 天圖表資料
    var recent     = bars.TakeLast(180).ToList();
    var rCloses    = recent.Select(b => b.Close).ToArray();
    var sma20Chart = IndicatorService.Sma(rCloses, 20);
    var sma60Chart = IndicatorService.Sma(rCloses, 60);

    var chart = new ChartData
    {
        Dates   = recent.Select(b => b.Date.ToString("yyyy-MM-dd")).ToList(),
        Prices  = recent.Select(b => (double?)Math.Round(b.Close, 2)).ToList(),
        Volumes = recent.Select(b => b.Volume).ToList(),
        Ma20    = sma20Chart.Select((v, i) => i >= 19 ? (double?)Math.Round(v, 2) : null).ToList(),
        Ma60    = sma60Chart.Select((v, i) => i >= 59 ? (double?)Math.Round(v, 2) : null).ToList(),
    };

    var result = new AnalysisResult
    {
        Ticker   = stockId,
        Name     = name,
        State    = state,
        Decision = decision,
        Chart    = chart,
        Matrix   = matrix.GetFullMatrix(),
    };

    return Results.Json(result, jsonOpts);
});

// ── 所有其他請求回傳 index.html（SPA fallback）────────────
app.MapFallbackToFile("index.html");

app.Run();
