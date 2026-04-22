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
builder.Services.AddHttpClient<VixService>();
builder.Services.AddHttpClient<UsMarketService>();
builder.Services.AddSingleton<MLPredictionService>();
builder.Services.AddHostedService<WarrantNewsService>();

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

// ── GET /api/vix ─────────────────────────────────────
app.MapGet("/api/vix", async (VixService vix) =>
{
    var snap = await vix.GetAsync();
    return Results.Json(snap, jsonOpts);
});

// ── GET /api/scan?tickers=2330,0050,... ─────────────────
app.MapGet("/api/scan", async (
    string? tickers,
    FinmindService finmind,
    StockCatalogService catalog,
    DecisionMatrixService matrix) =>
{
    if (string.IsNullOrWhiteSpace(tickers))
        return Results.Json(Array.Empty<object>(), jsonOpts);

    await catalog.EnsureLoadedAsync();

    var codes = tickers
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Take(50)
        .Select(t => catalog.ResolveToCode(t))
        .Distinct()
        .ToList();

    var sem = new SemaphoreSlim(3, 3);
    var bag = new System.Collections.Concurrent.ConcurrentBag<object>();

    var tasks = codes.Select(async code =>
    {
        await sem.WaitAsync();
        try
        {
            List<StockBar> bars;
            try { bars = await finmind.GetHistoryAsync(code); }
            catch { return; }

            if (bars.Count < 60) return;

            MarketState state;
            try { state = matrix.AnalyzeState(bars); }
            catch { return; }

            var decision = matrix.GetDecision(state.TrendState, state.VolState);
            var name = catalog.GetName(code);

            bag.Add(new
            {
                ticker        = code,
                name,
                trend_state   = state.TrendState,
                trend_label   = state.TrendLabel,
                vol_state     = state.VolState,
                vol_label     = state.VolLabel,
                vol_ratio     = Math.Round(state.VolRatio, 2),
                current_price = Math.Round(state.CurrentPrice, 2),
                trend_score   = state.TrendScore,
                adx           = state.Adx,
                action        = decision.Action,
                risk          = decision.Risk,
            });
        }
        finally { sem.Release(); }
    }).ToList();

    await Task.WhenAll(tasks);

    return Results.Json(bag.ToList(), jsonOpts);
});

// ── GET /api/scan/popular ────────────────────────────────
app.MapGet("/api/scan/popular", async (
    FinmindService finmind,
    StockCatalogService catalog,
    DecisionMatrixService matrix) =>
{
    await catalog.EnsureLoadedAsync();

    var popular = new[]
    {
        // ETF
        "0050","0056","006208","00878","00900","00919","00929",
        // 半導體/晶片
        "2330","2454","2303","3711","3034","5347","6770","2344","2408",
        // 科技/電子大廠
        "2317","2382","4938","2357","2308","2379","2376","3231","2353",
        "6669","2395","2345","2337","2301","2324","2360",
        // 通訊電信
        "2412","4904","3045",
        // 金融
        "2882","2881","2891","2886","2884","2885","2890","2892","5880",
        // 傳產/原物料
        "1301","1303","2002","6505","1402","2105",
        // 汽車/消費
        "2207","2201","1216","2912",
        // 其他知名
        "3008","2327","6415","5274","1590","2049",
    };

    var codes = popular.Select(c => catalog.ResolveToCode(c)).Distinct();
    var sem2  = new SemaphoreSlim(3, 3);
    var bag2  = new System.Collections.Concurrent.ConcurrentBag<object>();

    var tasks2 = codes.Select(async code =>
    {
        await sem2.WaitAsync();
        try
        {
            List<StockBar> bars;
            try { bars = await finmind.GetHistoryAsync(code); }
            catch { return; }

            if (bars.Count < 60) return;

            MarketState state;
            try { state = matrix.AnalyzeState(bars); }
            catch { return; }

            var decision = matrix.GetDecision(state.TrendState, state.VolState);
            var name = catalog.GetName(code);

            bag2.Add(new
            {
                ticker        = code,
                name,
                trend_state   = state.TrendState,
                trend_label   = state.TrendLabel,
                vol_state     = state.VolState,
                vol_label     = state.VolLabel,
                vol_ratio     = Math.Round(state.VolRatio, 2),
                current_price = Math.Round(state.CurrentPrice, 2),
                trend_score   = state.TrendScore,
                adx           = state.Adx,
                action        = decision.Action,
                risk          = decision.Risk,
            });
        }
        finally { sem2.Release(); }
    }).ToList();

    await Task.WhenAll(tasks2);

    return Results.Json(bag2.ToList(), jsonOpts);
});

// ── GET /api/warrants/news ──────────────────────────────
app.MapGet("/api/warrants/news", () =>
{
    var items = WarrantNewsService.GetNews();
    var last = WarrantNewsService.LastFetch;
    return Results.Json(new { last_fetch = last, news = items }, jsonOpts);
});

// ── GET /api/market/overnight ────────────────────────
app.MapGet("/api/market/overnight", async (UsMarketService us) =>
{
    var snap = await us.GetAsync();
    return Results.Json(snap, jsonOpts);
});

// ── GET /api/predict/{ticker} ─────────────────────
app.MapGet("/api/predict/{ticker}", async (
    string ticker,
    FinmindService finmind,
    StockCatalogService catalog,
    UsMarketService us,
    MLPredictionService ml) =>
{
    await catalog.EnsureLoadedAsync();
    var stockId = catalog.ResolveToCode(ticker.Trim());

    List<StockBar> bars;
    try { bars = await finmind.GetHistoryAsync(stockId); }
    catch (Exception ex) { return Results.Json(new { error = $"資料取得失敗：{ex.Message}" }, jsonOpts, statusCode: 500); }

    if (bars.Count < 35)
        return Results.Json(new { error = $"{stockId} 歷史資料不足（< 35 筆）" }, jsonOpts, statusCode: 404);

    var range = OpeningRangeService.Compute(bars);
    var pred  = ml.Predict(bars);
    var usSnap = await us.GetAsync();
    var name = catalog.GetName(stockId);

    return Results.Json(new
    {
        ticker = stockId,
        name,
        opening_range = range,
        ml_prediction = pred,
        us_overnight  = usSnap,
    }, jsonOpts);
});

// ── 所有其他請求回傳 index.html（SPA fallback）────────────
app.MapFallbackToFile("index.html");

app.Run();
