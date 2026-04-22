# -*- coding: utf-8 -*-
"""
訓練 Logistic Regression 方向預測模型，輸出 ml_weights.json 供 C# 載入。

特徵：
  returns_1d, returns_5d, returns_10d, rsi14, macd_hist_norm, vol_ratio_20d, atr_pct
標籤：
  next_close > today_close  -> 1, else 0

資料來源：Finmind 免費版 TaiwanStockPrice
"""

import json
import time
from datetime import datetime, timedelta

import numpy as np
import pandas as pd
import requests
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import train_test_split
from sklearn.preprocessing import StandardScaler

# 訓練股票池：流動性好的權值股 + 大型 ETF
TICKERS = [
    "0050", "0056", "00878",
    "2330", "2454", "2317", "2308", "2382",
    "2412", "2882", "2881", "2891",
    "1301", "1303", "3008", "2207",
    "1216", "2912", "2002", "2105",
]

FEATURE_NAMES = [
    "returns_1d", "returns_5d", "returns_10d",
    "rsi14", "macd_hist_norm", "vol_ratio_20d", "atr_pct"
]


def fetch_history(stock_id, days=720):
    end = datetime.today().strftime("%Y-%m-%d")
    start = (datetime.today() - timedelta(days=days)).strftime("%Y-%m-%d")
    url = f"https://api.finmindtrade.com/api/v4/data?dataset=TaiwanStockPrice&data_id={stock_id}&start_date={start}&end_date={end}"
    r = requests.get(url, timeout=30)
    r.raise_for_status()
    j = r.json()
    if j.get("status") != 200 or not j.get("data"):
        return None
    df = pd.DataFrame(j["data"])
    df = df.rename(columns={"max": "high", "min": "low", "Trading_Volume": "volume"})
    df["date"] = pd.to_datetime(df["date"])
    df = df.sort_values("date").reset_index(drop=True)
    return df


def rsi(close, period=14):
    delta = close.diff()
    gain = delta.where(delta > 0, 0.0)
    loss = (-delta).where(delta < 0, 0.0)
    avg_gain = gain.ewm(alpha=1 / period, adjust=False).mean()
    avg_loss = loss.ewm(alpha=1 / period, adjust=False).mean()
    rs = avg_gain / avg_loss.replace(0, np.nan)
    return 100 - 100 / (1 + rs)


def macd_hist(close, fast=12, slow=26, signal=9):
    ema_fast = close.ewm(span=fast, adjust=False).mean()
    ema_slow = close.ewm(span=slow, adjust=False).mean()
    macd = ema_fast - ema_slow
    sig = macd.ewm(span=signal, adjust=False).mean()
    return macd - sig


def atr(df, period=14):
    high = df["high"]; low = df["low"]; close = df["close"]
    prev_close = close.shift(1)
    tr = pd.concat([
        high - low,
        (high - prev_close).abs(),
        (low - prev_close).abs(),
    ], axis=1).max(axis=1)
    return tr.ewm(alpha=1 / period, adjust=False).mean()


def build_features(df):
    out = pd.DataFrame(index=df.index)
    out["returns_1d"]     = df["close"].pct_change(1)
    out["returns_5d"]     = df["close"].pct_change(5)
    out["returns_10d"]    = df["close"].pct_change(10)
    out["rsi14"]          = rsi(df["close"], 14)
    out["macd_hist_norm"] = macd_hist(df["close"]) / df["close"]
    out["vol_ratio_20d"]  = df["volume"] / df["volume"].rolling(20).mean()
    out["atr_pct"]        = atr(df, 14) / df["close"]
    out["label"]          = (df["close"].shift(-1) > df["close"]).astype(int)
    out = out.replace([np.inf, -np.inf], np.nan).dropna()
    # Clip extreme outliers (winsorize at 1st/99th percentile per feature)
    for col in FEATURE_NAMES:
        lo, hi = out[col].quantile([0.01, 0.99])
        out[col] = out[col].clip(lo, hi)
    return out


def main():
    print(f"Training on {len(TICKERS)} tickers...")
    all_frames = []
    for tid in TICKERS:
        try:
            df = fetch_history(tid)
            if df is None or len(df) < 100:
                print(f"  [skip] {tid}: insufficient data")
                continue
            feats = build_features(df)
            all_frames.append(feats)
            print(f"  [ok]   {tid}: {len(feats)} samples")
            time.sleep(0.4)
        except Exception as e:
            print(f"  [err]  {tid}: {e}")

    if not all_frames:
        raise RuntimeError("No data collected")

    data = pd.concat(all_frames, ignore_index=True)
    print(f"\nTotal samples: {len(data)}")
    print(f"Up rate (baseline): {data['label'].mean():.3f}")

    X = data[FEATURE_NAMES].values
    y = data["label"].values

    X_train, X_test, y_train, y_test = train_test_split(
        X, y, test_size=0.2, random_state=42, shuffle=True)

    scaler = StandardScaler().fit(X_train)
    X_train_s = scaler.transform(X_train)
    X_test_s  = scaler.transform(X_test)

    clf = LogisticRegression(C=1.0, max_iter=2000, random_state=42)
    clf.fit(X_train_s, y_train)

    train_acc = clf.score(X_train_s, y_train)
    test_acc  = clf.score(X_test_s, y_test)
    print(f"\nTrain accuracy: {train_acc:.4f}")
    print(f"Test  accuracy: {test_acc:.4f}")
    print(f"(baseline 50% = coin flip; 52~55% is realistic)")

    weights = {
        "intercept":    float(clf.intercept_[0]),
        "coef":         clf.coef_[0].tolist(),
        "features":     FEATURE_NAMES,
        "feature_mean": scaler.mean_.tolist(),
        "feature_std":  scaler.scale_.tolist(),
        "trained_at":   datetime.utcnow().strftime("%Y-%m-%d"),
        "samples":      len(data),
        "train_acc":    float(train_acc),
        "test_acc":     float(test_acc),
    }

    with open("ml_weights.json", "w", encoding="utf-8") as f:
        json.dump(weights, f, indent=2, ensure_ascii=False)
    print("\nSaved -> ml_weights.json")


if __name__ == "__main__":
    main()
