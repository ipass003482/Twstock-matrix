namespace StockMatrix.Models;

public record StockBar(
    DateTime Date,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume
);
