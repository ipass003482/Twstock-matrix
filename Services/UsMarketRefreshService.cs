namespace StockMatrix.Services;

/// <summary>
/// 每天台灣時間 05:00 自動重新抓取美股隔夜資料並重算偏多/偏空訊號。
/// 啟動時也會立即執行一次，確保 cache 有資料。
/// </summary>
public class UsMarketRefreshService : BackgroundService
{
    private static readonly TimeSpan TaiwanOffset = TimeSpan.FromHours(8);
    private readonly IServiceProvider _sp;
    private readonly ILogger<UsMarketRefreshService> _logger;

    public UsMarketRefreshService(IServiceProvider sp, ILogger<UsMarketRefreshService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowTw = DateTime.UtcNow.Add(TaiwanOffset);
            var nextTw = nowTw.Date.AddHours(5);
            if (nowTw >= nextTw) nextTw = nextTw.AddDays(1);

            var delay = nextTw - nowTw;
            _logger.LogInformation("UsMarketRefresh: next run at {Next} TWN (in {Delay:hh\\:mm})", nextTw.ToString("yyyy-MM-dd HH:mm"), delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
                await RefreshAsync();
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _sp.CreateScope();
            var us = scope.ServiceProvider.GetRequiredService<UsMarketService>();
            var snap = await us.ForceRefreshAsync();
            _logger.LogInformation("UsMarketRefresh: refreshed at {Now} TWN, signal={Signal} score={Score}",
                DateTime.UtcNow.Add(TaiwanOffset).ToString("yyyy-MM-dd HH:mm"),
                snap.Signal, snap.SignalScore);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UsMarketRefresh: refresh failed");
        }
    }
}
