# HermesProductParserFunc (C# Azure Function)

## 需求
1. 每分鐘執行一次 Timer Trigger
2. 用 Selenium 讀取 `https://www.hermes.com/tw/zh/category/leather-goods/bags-and-clutches/#|`
3. 目標元素：`span.header-title-current-number-result.ng-star-inserted`
4. 從類似 `(10)` 的字串中取 `10`

## 環境建置
1. 安裝 .NET 8 SDK
2. 安裝 Azure Functions Core Tools v4

## 執行步驟
1. 進入專案資料夾：
   ```bash
   cd c:\Users\TSAN-FENG\Desktop\HermesProductParserFunc
   ```
2. 下載套件：
   ```bash
   dotnet restore
   ```
3. 執行本地 function：
   ```bash
   func start
   ```

## Selenium 相關
- 已在 `HermesTimer.cs` 設定 `ChromeOptions` headless。
- Windows 本機需安裝 Chrome，且 `Selenium.Chrome.WebDriver` 會安裝對應 chromedriver。
- 若出現 driver 版本不符，請更新 Chrome 或 driver 套件版本。

## 範例輸出
- 會在 console log 顯示：
  - `HermesTimer executed at: ...`
  - `抓取到數字：10`
  - 或 `抓取失敗` 含 exception

## 執行結果檔案
- 每次 Timer 執行都會額外寫入 JSONL 檔案，預設位置是 `data/scrape-runs/hermes-scrape-YYYYMMDD.jsonl`。
- 若該次執行發生失敗或頁面狀態異常，會額外寫出 HTML snapshot 到 `data/scrape-runs/snapshots/`，方便後續比對實際頁面內容。
- 可用環境變數覆寫：
   - `SCRAPE_RUN_LOG_DIR`：指定結果檔案資料夾。
   - `SCRAPE_RUN_LOG_PATH`：直接指定結果檔案完整路徑。
