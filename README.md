# HermesProductParserFunc (.NET Worker Service)

## 需求
1. 每分鐘執行一次背景工作
2. 用 Selenium 讀取 `https://www.hermes.com/tw/zh/category/leather-goods/bags-and-clutches/#|`
3. 目標元素：`span.header-title-current-number-result.ng-star-inserted`
4. 從類似 `(10)` 的字串中取 `10`

## 環境建置
1. 安裝 .NET 8 SDK

## 執行步驟
1. 進入專案資料夾：
   ```bash
   cd c:\Users\TSAN-FENG\Desktop\HermesProductParserFunc
   ```
2. 下載套件：
   ```bash
   dotnet restore
   ```
3. 執行本地 worker：
   ```bash
   dotnet run
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

## Docker 部署
- 這個專案可用自訂容器部署到 Linux VM。Dockerfile 已包含 .NET 8 runtime、Google Chrome 與 Selenium 執行所需 Linux 相依套件。
- 建置映像：
   ```bash
   docker build -t hermes-product-parser .
   ```
- 啟動容器：
   ```bash
   docker run -d \
      --name hermes-product-parser \
      -v /opt/hermes/data:/home/data \
      -e USE_AZURE_SQL="1" \
      -e AZURE_SQL_CONN="<azure-sql-connection-string>" \
      -e INIT_DB="0" \
      -e LINE_BOT_CHANNEL_ACCESS_TOKEN="<line-token>" \
      -e SCRAPE_INTERVAL_SECONDS="60" \
      -e SCRAPE_RUN_LOG_DIR="/home/data/scrape-runs" \
      hermes-product-parser
   ```
- 若你只是第一次建表，可暫時把 `INIT_DB` 設成 `1`，建完後改回 `0`。
- 正式機建議使用 Azure SQL，不要依賴 SQLite。若仍要用 SQLite，至少把 `SQLITE_DB_PATH` 指向 `/home/data/hermes.db` 這類可寫入路徑。
   - 若要調整執行頻率，可透過 `SCRAPE_INTERVAL_SECONDS` 設定秒數，預設為 `60`。
