using StockMatrix.Models;

namespace StockMatrix.Services;

/// <summary>
/// 明日開盤參考區間：基於 ATR(14) 與過去 30 天平均隔夜跳空。
/// 純統計方法，不是預測，而是「68%/95% 機率落在此區間」的信賴區間。
/// </summary>
public static class OpeningRangeService
{
    public record OpeningRange(
        double LastClose,
        double AvgGapPct,
        double AtrPct,
        double Low68,
        double High68,
        double Low95,
        double High95,
        int SampleDays);

    public static OpeningRange? Compute(IReadOnlyList<StockBar> bars)
    {
        if (bars.Count < 35) return null;

        var arr = bars.ToArray();
        var atr = IndicatorService.Atr(arr, 14);
        var lastIdx = arr.Length - 1;
        var lastBar = arr[lastIdx];
        var lastClose = lastBar.Close;
        var lastAtr = atr[lastIdx];
        if (lastAtr <= 0 || lastClose <= 0) return null;

        // 過去 30 天的隔夜跳空：open[i] / close[i-1] - 1
        int lookback = Math.Min(30, arr.Length - 1);
        var gaps = new List<double>(lookback);
        for (int i = arr.Length - lookback; i < arr.Length; i++)
        {
            var prevClose = arr[i - 1].Close;
            if (prevClose <= 0) continue;
            gaps.Add(arr[i].Open / prevClose - 1.0);
        }
        if (gaps.Count == 0) return null;

        double avgGap = gaps.Average();
        double gapStd = Math.Sqrt(gaps.Select(g => (g - avgGap) * (g - avgGap)).Average());

        // 預期隔夜變化中心 = avgGap
        double center = lastClose * (1.0 + avgGap);
        // 1 sigma = sqrt(atrSigma^2 + gapStd^2 * lastClose^2)
        // 簡化：用 (gapStd * lastClose) 作為主要的隔夜不確定性
        double sigma = Math.Sqrt(Math.Pow(gapStd * lastClose, 2) + Math.Pow(lastAtr * 0.4, 2));

        return new OpeningRange(
            LastClose:  Math.Round(lastClose, 2),
            AvgGapPct:  Math.Round(avgGap * 100, 3),
            AtrPct:     Math.Round(lastAtr / lastClose * 100, 2),
            Low68:      Math.Round(center - sigma, 2),
            High68:     Math.Round(center + sigma, 2),
            Low95:      Math.Round(center - 2 * sigma, 2),
            High95:     Math.Round(center + 2 * sigma, 2),
            SampleDays: gaps.Count
        );
    }
}
