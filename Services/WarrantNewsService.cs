using System.Text.RegularExpressions;
using StockMatrix.Models;

namespace StockMatrix.Services;

public class WarrantNewsService : BackgroundService
{
    private static readonly List<WarrantNewsItem> _cache = new();
    private static readonly object _lock = new();
    private static string _lastFetch = string.Empty;

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<WarrantNewsService> _logger;

    // Taiwan is UTC+8 with no DST
    private static readonly TimeSpan TaiwanOffset = TimeSpan.FromHours(8);

    public WarrantNewsService(IHttpClientFactory factory, ILogger<WarrantNewsService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public static IReadOnlyList<WarrantNewsItem> GetNews()
    {
        lock (_lock) return _cache.AsReadOnly();
    }

    public static string LastFetch => _lastFetch;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Fetch once on startup so the page shows data immediately
        await FetchNewsAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowTw = DateTime.UtcNow.Add(TaiwanOffset);
            var nextRunTw = nowTw.Date.AddHours(1).AddMinutes(5);
            if (nowTw >= nextRunTw) nextRunTw = nextRunTw.AddDays(1);

            var delay = nextRunTw - nowTw;
            _logger.LogInformation("WarrantNews: next fetch at {Next} TWN (in {Delay:hh\\:mm})", nextRunTw.ToString("yyyy-MM-dd HH:mm"), delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
                await FetchNewsAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task FetchNewsAsync()
    {
        try
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://money.udn.com/");

            var html = await client.GetStringAsync("https://money.udn.com/money/cate/5590");
            var items = ParseWarrantNews(html);

            lock (_lock)
            {
                _cache.Clear();
                _cache.AddRange(items);
            }

            _lastFetch = DateTime.UtcNow.Add(TaiwanOffset).ToString("yyyy-MM-dd HH:mm");
            _logger.LogInformation("WarrantNews: fetched {Count} articles", items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WarrantNews: fetch failed");
        }
    }

    private static List<WarrantNewsItem> ParseWarrantNews(string html)
    {
        var items = new List<WarrantNewsItem>();

        var sectionMatch = Regex.Match(
            html,
            @"id=""sub_5739""(.*?)(?=id=""sub_\d+""|</body>)",
            RegexOptions.Singleline);

        if (!sectionMatch.Success) return items;

        var section = sectionMatch.Groups[1].Value;

        var articleRx = new Regex(
            @"href=""(/money/story/5739/[^""?]+)[^""]*""\s[^>]*title=""([^""]+)"".*?<time[^>]*>\s*([^<]+?)\s*</time>",
            RegexOptions.Singleline);

        var seen = new HashSet<string>();
        const int MaxItems = 8;

        foreach (Match m in articleRx.Matches(section))
        {
            var path = m.Groups[1].Value.Trim();
            var title = m.Groups[2].Value.Trim();
            var time = m.Groups[3].Value.Trim();

            if (!seen.Add(path)) continue;

            items.Add(new WarrantNewsItem(title, "https://money.udn.com" + path, time));
            if (items.Count >= MaxItems) break;
        }

        return items;
    }
}
