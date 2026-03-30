using StockMatrix.Models;

namespace StockMatrix.Services;

/// <summary>
/// 所有技術指標純計算，不依賴外部 I/O。
/// </summary>
public static class IndicatorService
{
    public static double[] Rsi(double[] closes, int period = 14)
    {
        var result = new double[closes.Length];
        if (closes.Length <= period) return result;

        var gains = new double[closes.Length];
        var losses = new double[closes.Length];
        for (int i = 1; i < closes.Length; i++)
        {
            var delta = closes[i] - closes[i - 1];
            gains[i]  = delta > 0 ? delta : 0;
            losses[i] = delta < 0 ? -delta : 0;
        }

        // 初始 SMA
        double avgGain = gains.Skip(1).Take(period).Average();
        double avgLoss = losses.Skip(1).Take(period).Average();

        for (int i = period; i < closes.Length; i++)
        {
            if (i != period)
            {
                avgGain = (avgGain * (period - 1) + gains[i]) / period;
                avgLoss = (avgLoss * (period - 1) + losses[i]) / period;
            }
            var rs = avgLoss == 0 ? 100 : avgGain / avgLoss;
            result[i] = 100 - 100 / (1 + rs);
        }
        return result;
    }

    public static (double[] Macd, double[] Signal, double[] Hist) MacdLine(
        double[] closes, int fast = 12, int slow = 26, int signal = 9)
    {
        var emaFast = Ema(closes, fast);
        var emaSlow = Ema(closes, slow);
        var macd    = closes.Select((_, i) => emaFast[i] - emaSlow[i]).ToArray();
        var sig     = Ema(macd, signal);
        var hist    = macd.Select((v, i) => v - sig[i]).ToArray();
        return (macd, sig, hist);
    }

    public static double[] Ema(double[] src, int period)
    {
        var result = new double[src.Length];
        double k = 2.0 / (period + 1);
        result[0] = src[0];
        for (int i = 1; i < src.Length; i++)
            result[i] = src[i] * k + result[i - 1] * (1 - k);
        return result;
    }

    public static double[] Sma(double[] src, int period)
    {
        var result = new double[src.Length];
        for (int i = period - 1; i < src.Length; i++)
            result[i] = src.Skip(i - period + 1).Take(period).Average();
        return result;
    }

    public static double[] Atr(StockBar[] bars, int period = 14)
    {
        var tr = new double[bars.Length];
        for (int i = 1; i < bars.Length; i++)
        {
            var hl = bars[i].High - bars[i].Low;
            var hc = Math.Abs(bars[i].High - bars[i - 1].Close);
            var lc = Math.Abs(bars[i].Low  - bars[i - 1].Close);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }
        // Wilder smoothing
        var atr = new double[bars.Length];
        if (bars.Length <= period) return atr;
        atr[period] = tr.Skip(1).Take(period).Average();
        for (int i = period + 1; i < bars.Length; i++)
            atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period;
        return atr;
    }

    public static (double[] Upper, double[] Mid, double[] Lower) Bollinger(
        double[] closes, int period = 20, double stdDev = 2)
    {
        var mid   = Sma(closes, period);
        var upper = new double[closes.Length];
        var lower = new double[closes.Length];
        for (int i = period - 1; i < closes.Length; i++)
        {
            var slice = closes.Skip(i - period + 1).Take(period).ToArray();
            var std   = StdDev(slice);
            upper[i]  = mid[i] + stdDev * std;
            lower[i]  = mid[i] - stdDev * std;
        }
        return (upper, mid, lower);
    }

    private static double StdDev(double[] values)
    {
        var avg = values.Average();
        return Math.Sqrt(values.Select(v => (v - avg) * (v - avg)).Average());
    }
}
