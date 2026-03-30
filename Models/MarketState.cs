namespace StockMatrix.Models;

public class MarketState
{
    public string TrendState { get; set; } = "";   // strong_bull / bull / sideways / bear / strong_bear
    public string TrendLabel { get; set; } = "";
    public string VolState { get; set; } = "";     // low / medium / high
    public string VolLabel { get; set; } = "";
    public double TrendScore { get; set; }
    public double CurrentPrice { get; set; }
    public double Ma20 { get; set; }
    public double Ma60 { get; set; }
    public double? Ma200 { get; set; }
    public double Rsi { get; set; }
    public double Macd { get; set; }
    public double MacdSignal { get; set; }
    public double MacdHist { get; set; }
    public double Atr { get; set; }
    public double AtrPct { get; set; }
    public double BbWidth { get; set; }
    public double VolRatio { get; set; }
}
