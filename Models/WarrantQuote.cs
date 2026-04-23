namespace StockMatrix.Models;

/// <summary>
/// 單檔權證即時/盤後資訊（取自 TWSE MI_INDEX type=0999）。
/// </summary>
public record WarrantQuote(
    string Code,            // 權證代號
    string Name,            // 權證名稱
    long Volume,            // 成交股數
    long TradeCount,        // 成交筆數
    long TradeValue,        // 成交金額 (TWD)
    double Close,           // 收盤價
    double Change,          // 漲跌價差
    string ChangeSign,      // + / - / 空字串
    string UnderlyingCode,  // 標的代號
    string UnderlyingName,  // 標的名稱
    double UnderlyingClose  // 標的收盤價
);
