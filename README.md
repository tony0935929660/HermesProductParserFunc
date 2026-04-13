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
- Windows 本機需安裝 Chrome。
- Docker image 建置時會一併安裝與 Chrome major version 對應的 `chromedriver`，避免 runtime 依賴 Selenium Manager 臨時下載 driver。
- 若本機執行出現 driver 版本不符，請更新本機 Chrome 或改用 Docker 執行。

## 範例輸出
- 會在 console log 顯示：
  - `HermesTimer executed at: ...`
  - `抓取到數字：10`
  - 或 `抓取失敗` 含 exception

## 執行結果檔案
- 每次 Timer 執行都會額外寫入 JSONL 檔案，預設位置是 `data/scrape-runs/hermes-scrape-YYYYMMDD.jsonl`。
- 若該次執行發生失敗或頁面狀態異常，會額外寫出 HTML snapshot 到 `data/scrape-runs/snapshots/`，方便後續比對實際頁面內容。
- 若該次執行發生失敗或頁面被 challenge 擋下，會額外寫出 screenshot 到 `data/scrape-runs/screenshots/`。
- 可用環境變數覆寫：
   - `SCRAPE_RUN_LOG_DIR`：指定結果檔案資料夾。
   - `SCRAPE_RUN_LOG_PATH`：直接指定結果檔案完整路徑。

## 本機用同一個 Docker 測試
- 若要在本機直接用 Linux 容器跑一輪並自動結束，可用：
   ```powershell
   docker build -t hermes-product-parser .
   docker run --rm --name hermes-product-parser \
      -v "${PWD}\data:/home/data" \
      -e USE_AZURE_SQL="0" \
      -e INIT_DB="0" \
      -e RUN_ONCE="1" \
      -e SQLITE_DB_PATH="/home/data/hermes.db" \
      -e SCRAPE_RUN_LOG_DIR="/home/data/scrape-runs" \
      -e SCRAPE_INTERVAL_SECONDS="300" \
      hermes-product-parser
   ```
- `RUN_ONCE=1` 會讓 worker 跑完第一輪後自動停止，適合本機驗證。
- 執行後可直接查看：
   - `data/scrape-runs/hermes-scrape-YYYYMMDD.jsonl`
   - `data/scrape-runs/screenshots/`
   - `data/scrape-runs/snapshots/`
- 若想改用 `docker compose` 做本機測試，可設定：
   ```powershell
   $env:HOST_DATA_DIR = "./data"
   $env:USE_AZURE_SQL = "0"
   $env:RUN_ONCE = "1"
   $env:RESTART_POLICY = "no"
   docker compose up --build
   ```

## Docker 部署
- 這個專案可用自訂容器部署到 Linux VM。Dockerfile 已包含 .NET 8 runtime、Google Chrome、相容版本的 `chromedriver`，以及 Selenium 執行所需 Linux 相依套件。
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
      -e RUN_ONCE="0" \
      -e LINE_BOT_CHANNEL_ACCESS_TOKEN="<line-token>" \
      -e SCRAPE_INTERVAL_SECONDS="60" \
      -e SCRAPE_RUN_LOG_DIR="/home/data/scrape-runs" \
      hermes-product-parser
   ```
- 若你只是第一次建表，可暫時把 `INIT_DB` 設成 `1`，建完後改回 `0`。
- 正式機建議使用 Azure SQL，不要依賴 SQLite。若仍要用 SQLite，至少把 `SQLITE_DB_PATH` 指向 `/home/data/hermes.db` 這類可寫入路徑。
   - 若要調整執行頻率，可透過 `SCRAPE_INTERVAL_SECONDS` 設定秒數，預設為 `60`。
   - `docker-compose.yml` 現在支援用 `HOST_DATA_DIR` 切換本機與 Linux VM 資料目錄；正式機可設成 `/opt/hermes/data`，本機可用 `./data`。
