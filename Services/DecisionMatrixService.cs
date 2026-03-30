using StockMatrix.Models;

namespace StockMatrix.Services;

public class DecisionMatrixService
{
    private static readonly Dictionary<(string Trend, string Vol), Decision> Matrix = new()
    {
        [("strong_bull", "low")]    = new() { Action="積極做多",         Description="趨勢強勢多頭且低波動，風險可控，建議積極加碼做多。",             Risk="低",   Level=5 },
        [("strong_bull", "medium")] = new() { Action="做多，注意停利",   Description="強勢多頭但波動中等，可做多但需設定停利保護獲利。",             Risk="中低", Level=4 },
        [("strong_bull", "high")]   = new() { Action="謹慎做多，控制部位", Description="多頭趨勢明確但波動偏高，小部位做多，嚴設停損。",             Risk="中",   Level=3 },
        [("bull", "low")]           = new() { Action="做多",             Description="多頭趨勢且低波動，可分批建立多方部位。",                       Risk="低中", Level=4 },
        [("bull", "medium")]        = new() { Action="分批做多",         Description="多頭趨勢中等波動，分批進場，設定合理停損。",                   Risk="中",   Level=3 },
        [("bull", "high")]          = new() { Action="減碼觀望",         Description="雖是多頭但波動劇烈，減少倉位等待波動收斂。",                   Risk="中高", Level=2 },
        [("sideways", "low")]       = new() { Action="區間操作",         Description="低波動整理格局，可做區間高賣低買或耐心等待突破。",             Risk="中",   Level=3 },
        [("sideways", "medium")]    = new() { Action="減碼觀望",         Description="方向不明確，降低倉位，等待明確訊號再行動。",                   Risk="中高", Level=2 },
        [("sideways", "high")]      = new() { Action="空手",             Description="高波動盤整方向不明，易被洗盤，建議空手觀望。",                 Risk="高",   Level=1 },
        [("bear", "low")]           = new() { Action="減碼或空手",       Description="空頭趨勢且低波動，應減少多方部位，可等待反彈再評估。",         Risk="中",   Level=2 },
        [("bear", "medium")]        = new() { Action="空手或做空",       Description="空頭趨勢中等波動，空手或小部位做空，嚴格停損。",               Risk="中高", Level=2 },
        [("bear", "high")]          = new() { Action="空手",             Description="空頭趨勢且高波動，不應逆勢做多，建議空手保本。",               Risk="高",   Level=1 },
        [("strong_bear", "low")]    = new() { Action="做空",             Description="強勢空頭低波動，趨勢明確向下，可做空或保持空手。",             Risk="低中", Level=1 },
        [("strong_bear", "medium")] = new() { Action="做空，嚴設停損",   Description="強勢空頭中等波動，可做空但須嚴格設定停損保護。",               Risk="中",   Level=1 },
        [("strong_bear", "high")]   = new() { Action="空手，等待止跌",   Description="強勢空頭高波動，風險極高，耐心等待止跌訊號後再操作。",         Risk="極高", Level=1 },
    };

    public Decision GetDecision(string trend, string vol) =>
        Matrix.TryGetValue((trend, vol), out var d) ? d
        : new Decision { Action="無法判斷", Description="狀態組合不在矩陣範圍內。", Risk="未知", Level=0 };

    public Dictionary<string, Decision> GetFullMatrix() =>
        Matrix.ToDictionary(kv => $"{kv.Key.Trend}_{kv.Key.Vol}", kv => kv.Value);

    public MarketState AnalyzeState(List<StockBar> bars)
    {
        var closes  = bars.Select(b => b.Close).ToArray();
        int n       = closes.Length;
        double last = closes[^1];

        double ma20  = Sma(closes, 20);
        double ma60  = Sma(closes, 60);
        double? ma200 = n >= 200 ? Sma(closes, 200) : null;

        var rsiArr   = IndicatorService.Rsi(closes, 14);
        double rsi   = rsiArr[^1];

        var (macdArr, sigArr, histArr) = IndicatorService.MacdLine(closes);
        double macdVal  = macdArr[^1];
        double sigVal   = sigArr[^1];
        double histVal  = histArr[^1];
        double prevHist = n >= 2 ? histArr[^2] : 0;

        var atrArr   = IndicatorService.Atr(bars.ToArray(), 14);
        double atr   = atrArr[^1];
        double atrPct = atr / last * 100;

        var sma20Arr = IndicatorService.Sma(closes, 20);
        var (bbUpper, bbMid, bbLower) = IndicatorService.Bollinger(closes);
        double bbWidth = bbMid[^1] > 0
            ? (bbUpper[^1] - bbLower[^1]) / bbMid[^1] * 100 : 0;

        var volArr  = bars.Select(b => (double)b.Volume).ToArray();
        double vAvg = Sma(volArr, 20);
        double vCur = volArr[^1];
        double vRatio = vAvg > 0 ? vCur / vAvg : 1.0;

        // 趨勢評分
        double score = 0;
        score += last > ma20 ? 1.0 : -1.0;
        score += last > ma60 ? 1.0 : -1.0;
        if (ma200.HasValue)
        {
            score += last > ma200.Value ? 1.5 : -1.5;
            var ma20v = sma20Arr[^1];
            score += ma20v > ma60 && ma60 > ma200.Value ?  2.0
                   : ma20v < ma60 && ma60 < ma200.Value ? -2.0 : 0;
        }
        else
        {
            score += sma20Arr[^1] > ma60 ? 1.0 : -1.0;
        }

        score += rsi switch
        {
            >= 70 =>  1.5, >= 60 =>  1.0, >= 50 =>  0.3,
            >= 40 => -0.3, >= 30 => -1.0, _     => -1.5
        };

        score += macdVal > sigVal ? 0.8 : -0.8;
        if (histVal > 0 && prevHist < 0)  score += 0.5;
        if (histVal < 0 && prevHist > 0)  score -= 0.5;

        var (trendState, trendLabel) = score switch
        {
            >= 4.5  => ("strong_bull",  "強勢多頭"),
            >= 1.5  => ("bull",         "多頭"),
            <= -4.5 => ("strong_bear",  "強勢空頭"),
            <= -1.5 => ("bear",         "空頭"),
            _       => ("sideways",     "盤整"),
        };

        var (volState, volLabel) = atrPct switch
        {
            < 1.5 => ("low",    "低波動"),
            < 3.0 => ("medium", "中波動"),
            _     => ("high",   "高波動"),
        };

        return new MarketState
        {
            TrendState   = trendState,   TrendLabel = trendLabel,
            VolState     = volState,     VolLabel   = volLabel,
            TrendScore   = Math.Round(score, 2),
            CurrentPrice = Math.Round(last, 2),
            Ma20         = Math.Round(ma20, 2),
            Ma60         = Math.Round(ma60, 2),
            Ma200        = ma200.HasValue ? Math.Round(ma200.Value, 2) : null,
            Rsi          = Math.Round(rsi, 2),
            Macd         = Math.Round(macdVal, 4),
            MacdSignal   = Math.Round(sigVal, 4),
            MacdHist     = Math.Round(histVal, 4),
            Atr          = Math.Round(atr, 2),
            AtrPct       = Math.Round(atrPct, 2),
            BbWidth      = Math.Round(bbWidth, 2),
            VolRatio     = Math.Round(vRatio, 2),
        };
    }

    private static double Sma(double[] src, int period)
    {
        int start = Math.Max(0, src.Length - period);
        return src.Skip(start).Average();
    }
}
