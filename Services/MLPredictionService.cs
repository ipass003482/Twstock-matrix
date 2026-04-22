using System.Text.Json;
using StockMatrix.Models;

namespace StockMatrix.Services;

/// <summary>
/// 機器學習方向預測：載入 Python 訓練的 Logistic Regression 權重 (weights.json)，
/// 對「明日收盤 &gt; 今日收盤」進行二元分類。
/// 特徵：returns_1d, returns_5d, returns_10d, rsi14, macd_hist_norm, vol_ratio_20d, atr_pct
/// </summary>
public class MLPredictionService
{
    private readonly ILogger<MLPredictionService> _logger;
    private readonly Weights? _weights;

    public record FeatureSet(
        double Returns1d,
        double Returns5d,
        double Returns10d,
        double Rsi14,
        double MacdHistNorm,
        double VolRatio20d,
        double AtrPct);

    public record Prediction(
        double UpProbability,
        double DownProbability,
        string Direction,        // up / down / neutral
        string DirectionLabel,
        string Confidence,       // low / medium / high
        FeatureSet Features,
        string ModelInfo);

    private record Weights(
        double Intercept,
        double[] Coef,
        string[] Features,
        double[] FeatureMean,
        double[] FeatureStd,
        string TrainedAt,
        int Samples,
        double TrainAcc,
        double TestAcc);

    public bool IsAvailable => _weights != null;

    public MLPredictionService(ILogger<MLPredictionService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        var path = Path.Combine(env.ContentRootPath, "ml_weights.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("ml_weights.json not found, ML predictor disabled");
            return;
        }
        try
        {
            var json = File.ReadAllText(path);
            _weights = JsonSerializer.Deserialize<Weights>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
            _logger.LogInformation("ML model loaded: trained {When}, samples={N}, test_acc={Acc:F3}",
                _weights?.TrainedAt, _weights?.Samples, _weights?.TestAcc ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ml_weights.json");
        }
    }

    public Prediction? Predict(IReadOnlyList<StockBar> bars)
    {
        if (_weights == null) return null;
        var features = ExtractFeatures(bars);
        if (features == null) return null;

        var x = new[]
        {
            features.Returns1d,
            features.Returns5d,
            features.Returns10d,
            features.Rsi14,
            features.MacdHistNorm,
            features.VolRatio20d,
            features.AtrPct,
        };

        // Standardize
        var z = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            var std = _weights.FeatureStd[i];
            z[i] = std > 0 ? (x[i] - _weights.FeatureMean[i]) / std : 0;
        }

        // Linear combination
        double linear = _weights.Intercept;
        for (int i = 0; i < z.Length; i++) linear += _weights.Coef[i] * z[i];

        // Sigmoid
        double upProb = 1.0 / (1.0 + Math.Exp(-linear));
        double downProb = 1.0 - upProb;

        string direction, label, confidence;
        if (upProb >= 0.55) { direction = "up"; label = "偏多"; }
        else if (upProb <= 0.45) { direction = "down"; label = "偏空"; }
        else { direction = "neutral"; label = "中性"; }

        var spread = Math.Abs(upProb - 0.5);
        if (spread >= 0.10) confidence = "high";
        else if (spread >= 0.05) confidence = "medium";
        else confidence = "low";

        return new Prediction(
            UpProbability: Math.Round(upProb, 4),
            DownProbability: Math.Round(downProb, 4),
            Direction: direction,
            DirectionLabel: label,
            Confidence: confidence,
            Features: features,
            ModelInfo: $"LogReg / 訓練 {_weights.TrainedAt} / 測試集準確率 {_weights.TestAcc:P1}"
        );
    }

    public static FeatureSet? ExtractFeatures(IReadOnlyList<StockBar> bars)
    {
        if (bars.Count < 30) return null;
        var arr = bars.ToArray();
        var closes = arr.Select(b => b.Close).ToArray();
        int last = arr.Length - 1;

        var rsi = IndicatorService.Rsi(closes, 14);
        var (_, _, hist) = IndicatorService.MacdLine(closes);
        var atr = IndicatorService.Atr(arr, 14);

        double r1  = closes[last] / closes[last - 1] - 1;
        double r5  = closes[last] / closes[last - 5] - 1;
        double r10 = closes[last] / closes[last - 10] - 1;

        double avgVol20 = arr.Skip(arr.Length - 21).Take(20).Average(b => (double)b.Volume);
        double volRatio = avgVol20 > 0 ? arr[last].Volume / avgVol20 : 1.0;

        double atrPct = closes[last] > 0 ? atr[last] / closes[last] : 0;
        double macdNorm = closes[last] > 0 ? hist[last] / closes[last] : 0;

        return new FeatureSet(
            Returns1d:    r1,
            Returns5d:    r5,
            Returns10d:   r10,
            Rsi14:        rsi[last],
            MacdHistNorm: macdNorm,
            VolRatio20d:  volRatio,
            AtrPct:       atrPct
        );
    }
}
