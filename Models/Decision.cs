namespace StockMatrix.Models;

public class Decision
{
    public string Action { get; set; } = "";
    public string Description { get; set; } = "";
    public string Risk { get; set; } = "";
    public int Level { get; set; }
}

public class AnalysisResult
{
    public string Ticker { get; set; } = "";
    public string Name { get; set; } = "";
    public MarketState State { get; set; } = null!;
    public Decision Decision { get; set; } = null!;
    public ChartData Chart { get; set; } = null!;
    public Dictionary<string, Decision> Matrix { get; set; } = null!;
}

public class ChartData
{
    public List<string> Dates { get; set; } = [];
    public List<double?> Prices { get; set; } = [];
    public List<long> Volumes { get; set; } = [];
    public List<double?> Ma20 { get; set; } = [];
    public List<double?> Ma60 { get; set; } = [];
}

public record StockSearchItem(string Code, string Name);
