using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace StockMatrix.Services;

public record VixSnapshot(double? UsVix, double? TwVix, string UpdatedAt);

public class VixService
{
    private readonly HttpClient _http;

    public VixService(HttpClient http)
    {
        _http = http;
    }

    public async Task<VixSnapshot> GetAsync()
    {
        var usTask = FetchUsVixAsync();
        var twTask = FetchTwVixAsync();
        await Task.WhenAll(usTask, twTask);
        return new VixSnapshot(await usTask, await twTask,
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC");
    }

    // ── US VIX via stooq.com (fallback: Yahoo Finance) ────────────────
    private async Task<double?> FetchUsVixAsync()
    {
        try
        {
            var end   = DateTime.Today.ToString("yyyyMMdd");
            var start = DateTime.Today.AddDays(-10).ToString("yyyyMMdd");
            var url   = $"https://stooq.com/q/d/l/?s=%5Evix&d1={start}&d2={end}&i=d";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var csv = await resp.Content.ReadAsStringAsync();
                var val = ParseStooqCsv(csv);
                if (val.HasValue) return val;
            }
        }
        catch { /* fallthrough to Yahoo */ }

        return await FallbackYahooVixAsync();
    }

    private async Task<double?> FallbackYahooVixAsync()
    {
        try
        {
            var url = "https://query1.finance.yahoo.com/v8/finance/chart/%5EVIX?range=5d&interval=1d";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            req.Headers.Add("Accept", "application/json");

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var closes = doc.RootElement
                .GetProperty("chart")
                .GetProperty("result")[0]
                .GetProperty("indicators")
                .GetProperty("quote")[0]
                .GetProperty("close");

            double? last = null;
            foreach (var v in closes.EnumerateArray())
                if (v.ValueKind != JsonValueKind.Null)
                    last = Math.Round(v.GetDouble(), 2);

            return last;
        }
        catch { return null; }
    }

    // ── Taiwan VIX via TAIFEX SockJS WebSocket (free, official source) ──
    private async Task<double?> FetchTwVixAsync()
    {
        // TAIFEX real-time feed uses SockJS. The WebSocket transport path is:
        // wss://mis.taifex.com.tw/futures/rt/{server}/{session}/websocket
        // SockJS framing: 'o' = open, 'a[...]' = data, 'h' = heartbeat
        // Subscribe message format: ["{"type":"subscribe","symbols":["TVIX"]}"]
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var wsUri = new Uri($"wss://mis.taifex.com.tw/futures/rt/000/{sessionId}/websocket");

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://mis.taifex.com.tw");
        ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await ws.ConnectAsync(wsUri, cts.Token);

            var buf = new byte[8192];
            var seg = new ArraySegment<byte>(buf);

            // Read the SockJS open frame ('o')
            var result = await ws.ReceiveAsync(seg, cts.Token);
            var frame  = Encoding.UTF8.GetString(buf, 0, result.Count);
            if (!frame.StartsWith("o")) return null;

            // Send subscribe for TVIX (Taiwan VIX index symbol on TAIFEX)
            // SockJS frame format: ["JSON-encoded-string"]
            var innerMsg = JsonSerializer.Serialize(new { type = "subscribe", symbols = new[] { "TVIX" } });
            var subMsg   = JsonSerializer.Serialize(new[] { innerMsg });  // ["..."]
            var subBytes = Encoding.UTF8.GetBytes(subMsg);
            await ws.SendAsync(new ArraySegment<byte>(subBytes),
                WebSocketMessageType.Text, true, cts.Token);

            // Read up to 3 frames looking for quote data
            for (int i = 0; i < 3; i++)
            {
                result = await ws.ReceiveAsync(seg, cts.Token);
                frame  = Encoding.UTF8.GetString(buf, 0, result.Count);

                if (frame.StartsWith("h")) continue; // heartbeat
                if (!frame.StartsWith("a")) continue;

                // SockJS data frame: a["json-string"]
                var inner = frame[2..^1]; // strip 'a[' and ']'
                inner = inner.Trim('"').Replace("\\\"", "\"").Replace("\\\\", "\\");

                using var doc = JsonDocument.Parse(inner);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl) ||
                    typeEl.GetString() != "quote") continue;

                if (!root.TryGetProperty("quote", out var quoteEl)) continue;
                if (!quoteEl.TryGetProperty("values", out var valuesEl)) continue;

                // Try common field names for the VIX index value
                foreach (var field in new[] { "IndexValue", "Close", "Price", "LastPrice",
                                               "close", "price", "value", "last" })
                {
                    if (valuesEl.TryGetProperty(field, out var el))
                    {
                        if (el.ValueKind == JsonValueKind.Number)
                            return Math.Round(el.GetDouble(), 2);
                        if (el.ValueKind == JsonValueKind.String &&
                            double.TryParse(el.GetString(),
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var dv))
                            return Math.Round(dv, 2);
                    }
                }
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "",
                    System.Threading.CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ── CSV parser for stooq format: Date,Open,High,Low,Close,Volume ──
    private static double? ParseStooqCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;

        var lastLine = lines[^1];
        var parts    = lastLine.Split(',');
        if (parts.Length >= 5 &&
            double.TryParse(parts[4],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var close))
        {
            return Math.Round(close, 2);
        }
        return null;
    }
}
