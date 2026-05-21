namespace StockMatrix.Services;

internal static class FinmindAuth
{
    public static readonly string? Token = Environment.GetEnvironmentVariable("FINMIND_TOKEN");

    public static string Append(string url) =>
        string.IsNullOrEmpty(Token) ? url : url + "&token=" + Uri.EscapeDataString(Token);
}
