using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace HermesProductParserFunc.Functions
{
    public class Product
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Price { get; set; }
        public string ImageUrl { get; set; }
        public string Color { get; set; }
    }

    public class HermesTimer
    {
        private readonly ILogger _logger;

        public HermesTimer(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<HermesTimer>();
        }

        [Function("HermesTimer")]
        public async Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo timerInfo)
        {
            IProductRepository repo;
            var useAzureSql = Environment.GetEnvironmentVariable("USE_AZURE_SQL") == "1";
            if (useAzureSql)
                repo = new AzureSqlProductRepository(Environment.GetEnvironmentVariable("AZURE_SQL_CONN"));
            else
                repo = new SqliteProductRepository();

            var repositoryMode = useAzureSql ? "AzureSql" : "Sqlite";
            var runStartedAtUtc = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var runRecord = new ScrapeRunRecord
            {
                RunId = Guid.NewGuid().ToString("n"),
                StartedAtUtc = runStartedAtUtc,
                RepositoryMode = repositoryMode,
                Url = "https://www.hermes.com/tw/zh/category/leather-goods/bags-and-clutches/#|"
            };
            var runFileLogger = new ScrapeRunFileLogger();
            string htmlSnapshot = null;

            _logger.LogInformation("Repository mode: {repositoryMode}", repositoryMode);

            bool needInitDb = Environment.GetEnvironmentVariable("INIT_DB") == "1";
            try
            {
                if (needInitDb)
                {
                    repo.InitDb();
                }

                _logger.LogInformation($"HermesTimer executed at: {runStartedAtUtc:O}");

                var options = new ChromeOptions();
                options.AddArgument("--headless=new");
                options.AddArgument("--no-sandbox");
                options.AddArgument("--disable-gpu");
                options.AddArgument("--disable-dev-shm-usage");
                options.AddArgument("--disable-extensions");
                options.AddArgument("--disable-software-rasterizer");
                options.AddArgument("--remote-debugging-port=9222");
                options.AddArgument("--disable-setuid-sandbox");
                options.AddArgument("--enable-automation");
                options.AddArgument("--window-size=1920,1080");
                options.AddArgument("--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.7680.66 Safari/537.36");

                var chromePath = ResolveChromeBinaryPath();
                var chromePathExists = !string.IsNullOrWhiteSpace(chromePath) && System.IO.File.Exists(chromePath);
                if (chromePathExists)
                {
                    options.BinaryLocation = chromePath;
                }

                var service = ChromeDriverService.CreateDefaultService();
                service.SuppressInitialDiagnosticInformation = true;
                service.HideCommandPromptWindow = true;

                _logger.LogInformation("Chrome runtime info: OS={os}, Arch={arch}, BaseDir={baseDir}, CurrentDir={currentDir}, ChromePathExists={chromePathExists}, ChromeBinary={chromeBinary}",
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.OSArchitecture,
                    AppContext.BaseDirectory,
                    Environment.CurrentDirectory,
                    chromePathExists,
                    string.IsNullOrWhiteSpace(options.BinaryLocation) ? "<default>" : options.BinaryLocation);

                ChromeDriver driver;
                try
                {
                    driver = new ChromeDriver(service, options, TimeSpan.FromSeconds(60));
                }
                catch (Exception ex)
                {
                    ApplyFailure(runRecord, "ChromeDriver 初始化失敗", ex);
                    _logger.LogError(ex, "ChromeDriver 初始化失敗");
                    throw;
                }

                using (driver)
                {
                    driver.Navigate().GoToUrl(runRecord.Url);

                    var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));

                    wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").ToString() == "complete");

                    runRecord.CurrentPageUrl = driver.Url;
                    _logger.LogInformation($"[DEBUG] After readyState, driver.Url: {driver.Url}");
                    var ps1 = SafeGetPageSource(driver);
                    if (!string.IsNullOrEmpty(ps1))
                    {
                        _logger.LogInformation($"[DEBUG] PageSource前2000字: {ps1.Substring(0, Math.Min(2000, ps1.Length))}");
                    }

                    int expectedCount = 0;
                    try
                    {
                        var headerElem = wait.Until(drv => drv.FindElement(By.CssSelector(".header-title-current-number-result")));
                        var numText = headerElem?.Text?.Trim();
                        var match = Regex.Match(numText ?? string.Empty, @"\d+");
                        if (match.Success)
                            int.TryParse(match.Value, out expectedCount);
                        _logger.LogInformation($"[DEBUG] 預期商品數量: {expectedCount} (原始: {numText})");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"[DEBUG] 無法取得 header-title-current-number-result: {ex.Message}");
                    }

                    runRecord.ExpectedCount = expectedCount;
                    if (expectedCount == 0)
                    {
                        runRecord.Outcome = "UnexpectedPageState";
                        runRecord.Message = "預期商品數量為 0，可能無法正確爬取";
                        runRecord.CurrentPageUrl = driver.Url;
                        htmlSnapshot = SafeGetPageSource(driver);
                        _logger.LogWarning("[DEBUG] 預期商品數量為 0，可能無法正確爬取");
                        return;
                    }

                    try
                    {
                        wait.Until(d =>
                        {
                            var titles = d.FindElements(By.CssSelector(".product-title"));
                            var prices = d.FindElements(By.CssSelector(".product-item-price"));
                            var colors = d.FindElements(By.CssSelector(".product-item-colors"));
                            return titles.Count == expectedCount && prices.Count == expectedCount && colors.Count == expectedCount;
                        });
                    }
                    catch (Exception ex)
                    {
                        runRecord.CurrentPageUrl = driver.Url;
                        htmlSnapshot = SafeGetPageSource(driver);
                        _logger.LogError($"[DEBUG] .product-item 數量未達預期, driver.Url: {driver.Url}");
                        if (!string.IsNullOrEmpty(htmlSnapshot))
                        {
                            _logger.LogError($"[DEBUG] PageSource前2000字: {htmlSnapshot.Substring(0, Math.Min(2000, htmlSnapshot.Length))}");
                        }

                        ApplyFailure(runRecord, ".product-item 數量未達預期", ex);
                        throw;
                    }

                    var products = new List<Product>();
                    try
                    {
                        var productElements = driver.FindElements(By.CssSelector(".product-item"));
                        if (productElements.Count == 0)
                        {
                            htmlSnapshot = SafeGetPageSource(driver);
                            if (!string.IsNullOrEmpty(htmlSnapshot))
                            {
                                _logger.LogWarning($"PageSource前2000字: {htmlSnapshot.Substring(0, Math.Min(2000, htmlSnapshot.Length))}");
                            }
                        }

                        foreach (var productElement in productElements)
                        {
                            try
                            {
                                string title = null, price = null, color = null;

                                for (int retry = 0; retry < 3; retry++)
                                {
                                    try
                                    {
                                        var titleElement = productElement.FindElement(By.CssSelector(".product-title"));
                                        title = titleElement?.GetAttribute("textContent")?.Trim();
                                        if (string.IsNullOrWhiteSpace(title))
                                            title = titleElement?.GetAttribute("innerText")?.Trim();
                                        if (string.IsNullOrWhiteSpace(title))
                                            title = titleElement?.Text?.Trim();
                                    }
                                    catch
                                    {
                                    }

                                    try
                                    {
                                        var priceElement = productElement.FindElement(By.CssSelector(".price.small"));
                                        var priceText = priceElement?.GetAttribute("textContent")?.Trim();
                                        if (string.IsNullOrWhiteSpace(priceText))
                                            priceText = priceElement?.GetAttribute("innerText")?.Trim();
                                        if (string.IsNullOrWhiteSpace(priceText))
                                            priceText = priceElement?.Text?.Trim();

                                        var priceMatch = Regex.Match(priceText ?? string.Empty, @"NT\$\s*[\d,]+");
                                        if (priceMatch.Success)
                                            price = priceMatch.Value.Trim();
                                        else
                                            price = priceText?.Replace("Price :", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                                    }
                                    catch
                                    {
                                    }

                                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(price))
                                        break;

                                    System.Threading.Thread.Sleep(500);
                                }

                                try
                                {
                                    var colorElement = productElement.FindElement(By.CssSelector(".product-item-colors"));
                                    color = colorElement?.GetAttribute("textContent")?.Trim();
                                    if (string.IsNullOrWhiteSpace(color))
                                        color = colorElement?.GetAttribute("innerText")?.Trim();
                                    if (string.IsNullOrWhiteSpace(color))
                                        color = colorElement?.Text?.Trim();
                                    if (!string.IsNullOrEmpty(color) && color.Contains(":"))
                                        color = color.Substring(color.IndexOf(":") + 1).Trim();
                                }
                                catch
                                {
                                }

                                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(price))
                                {
                                    string outerHtml = string.Empty;
                                    try { outerHtml = productElement.GetAttribute("outerHTML"); } catch { }
                                    _logger.LogWarning($"商品信息不完整，跳過 - Title: '{title}', Price: '{price}'，outerHTML: {outerHtml}");
                                    continue;
                                }

                                string imageUrl = null;
                                try
                                {
                                    var images = productElement.FindElements(By.TagName("img"));
                                    foreach (var img in images)
                                    {
                                        var dataSrc = img.GetAttribute("data-src");
                                        var src = img.GetAttribute("src");
                                        if (!string.IsNullOrEmpty(dataSrc))
                                        {
                                            imageUrl = dataSrc;
                                            break;
                                        }

                                        if (!string.IsNullOrEmpty(src) && !src.Contains("base64") && !src.Contains("placeholder"))
                                        {
                                            imageUrl = src;
                                            break;
                                        }
                                    }

                                    if (string.IsNullOrEmpty(imageUrl) && images.Count > 0)
                                    {
                                        _logger.LogWarning($"找不到有效圖片，img HTML: {images[0].GetAttribute("outerHTML")}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning($"查找圖片失敗: {ex.Message}");
                                }

                                if (!string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(price))
                                {
                                    string id = null;
                                    try
                                    {
                                        var match = Regex.Match(imageUrl ?? string.Empty, @"hermesproduct/([^/]+)_front");
                                        if (match.Success)
                                            id = match.Groups[1].Value;
                                        else
                                            _logger.LogWarning($"id parse failed for imageUrl: {imageUrl}");
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning($"id parse exception: {ex.Message}");
                                    }

                                    products.Add(new Product
                                    {
                                        Id = id,
                                        Title = title,
                                        Price = price,
                                        ImageUrl = imageUrl,
                                        Color = color
                                    });
                                }
                                else
                                {
                                    _logger.LogWarning($"✗ 商品資料不完整: Title={!string.IsNullOrEmpty(title)}, Price={!string.IsNullOrEmpty(price)}, ImageUrl={!string.IsNullOrEmpty(imageUrl)}, Color={!string.IsNullOrEmpty(color)}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"爬取單個商品失敗: {ex.Message}");
                                _logger.LogInformation($"例外詳情: {ex}");
                            }
                        }

                        runRecord.ScrapedCount = products.Count;
                        runRecord.CurrentPageUrl = driver.Url;

                        if (products.Count > 0)
                        {
                            var oldList = repo.GetAllProducts();
                            var newProducts = products
                                .Where(n => !oldList.Any(o => o.Title == n.Title && o.Id == n.Id && o.Color == n.Color))
                                .ToList();

                            var deletedProducts = oldList
                                .Where(o => !products.Any(n => n.Title == o.Title && n.Id == o.Id && n.Color == o.Color))
                                .ToList();

                            runRecord.NewProductsCount = newProducts.Count;
                            runRecord.DeletedProductsCount = deletedProducts.Count;
                            runRecord.PersistedCount = oldList.Count;

                            if (newProducts.Count > 0 || deletedProducts.Count > 0)
                            {
                                repo.ClearAllProducts();
                                repo.InsertAllProducts(products);
                                runRecord.PersistedCount = products.Count;
                                _logger.LogInformation($"成功更新資料庫，共 {products.Count} 個商品");
                            }

                            if (newProducts.Count > 0)
                            {
                                await BroadcastLineMessageAsync(newProducts);
                                runRecord.BroadcastCount = newProducts.Count;
                            }

                            runRecord.Outcome = "Success";
                            runRecord.Message = newProducts.Count > 0 || deletedProducts.Count > 0
                                ? $"成功更新資料庫，共 {products.Count} 個商品"
                                : $"資料無變更，共 {products.Count} 個商品";
                        }
                        else
                        {
                            runRecord.Outcome = "NoProducts";
                            runRecord.Message = "未爬取到任何商品";
                            htmlSnapshot = SafeGetPageSource(driver);
                            _logger.LogWarning("未爬取到任何商品");
                        }
                    }
                    catch (Exception ex)
                    {
                        runRecord.CurrentPageUrl = driver.Url;
                        htmlSnapshot ??= SafeGetPageSource(driver);
                        ApplyFailure(runRecord, "爬取商品列表失敗", ex);
                        _logger.LogError(ex, "爬取商品列表失敗");
                    }
                }
            }
            catch (Exception ex)
            {
                if (string.IsNullOrWhiteSpace(runRecord.Outcome))
                {
                    ApplyFailure(runRecord, "抓取失敗", ex);
                }

                _logger.LogError(ex, "抓取失敗");
            }
            finally
            {
                stopwatch.Stop();
                runRecord.CompletedAtUtc = DateTime.UtcNow;
                runRecord.DurationMs = stopwatch.ElapsedMilliseconds;
                if (string.IsNullOrWhiteSpace(runRecord.Outcome))
                {
                    runRecord.Outcome = "Unknown";
                    runRecord.Message = "執行結束，但未產生可判定的結果";
                }

                await runFileLogger.PersistAsync(runRecord, htmlSnapshot);
                _logger.LogInformation("執行結果已寫入檔案: {logFilePath}", runRecord.LogFilePath);

                if (!string.IsNullOrWhiteSpace(runRecord.HtmlSnapshotPath))
                {
                    _logger.LogInformation("HTML snapshot 已寫入: {snapshotPath}", runRecord.HtmlSnapshotPath);
                }
            }
        }

        private static void ApplyFailure(ScrapeRunRecord runRecord, string message, Exception ex)
        {
            runRecord.Outcome = "Failed";
            runRecord.Message = message;
            runRecord.ErrorType = ex.GetType().FullName;
            runRecord.ErrorMessage = ex.Message;
        }

        private static string SafeGetPageSource(IWebDriver driver)
        {
            try
            {
                return driver.PageSource;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveChromeBinaryPath()
        {
            var configuredChromePath = Environment.GetEnvironmentVariable("CHROME_BIN");
            if (string.IsNullOrWhiteSpace(configuredChromePath))
            {
                configuredChromePath = Environment.GetEnvironmentVariable("CHROME_PATH");
            }

            if (!string.IsNullOrWhiteSpace(configuredChromePath) && System.IO.File.Exists(configuredChromePath))
            {
                return configuredChromePath;
            }

            var candidatePaths = new[]
            {
                "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
                "/usr/bin/google-chrome",
                "/usr/bin/google-chrome-stable",
                "/usr/bin/chromium",
                "/usr/bin/chromium-browser"
            };

            return candidatePaths.FirstOrDefault(System.IO.File.Exists);
        }

        private async Task BroadcastLineMessageAsync(List<Product> products)
        {
            var token = Environment.GetEnvironmentVariable("LINE_BOT_CHANNEL_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("環境變數 LINE_BOT_CHANNEL_ACCESS_TOKEN 未設定，無法使用 LINE Messaging API 廣播。");
                return;
            }

            try
            {
                var bubbles = products.Select(p => new
                {
                    type = "bubble",
                    hero = new
                    {
                        type = "image",
                        size = "full",
                        aspectRatio = "1:1",
                        aspectMode = "cover",
                        url = p.ImageUrl
                    },
                    body = new
                    {
                        type = "box",
                        layout = "vertical",
                        spacing = "md",
                        contents = new object[]
                        {
                            new
                            {
                                type = "text",
                                text = p.Title,
                                weight = "bold",
                                wrap = true,
                                size = "sm"
                            },
                            new
                            {
                                type = "text",
                                text = p.Price,
                                color = "#999999",
                                size = "xs"
                            }
                        }
                    }
                }).ToList();

                var payload = new
                {
                    messages = new[]
                    {
                        new
                        {
                            type = "flex",
                            altText = $"Hermes 皮件商品 - 共 {products.Count} 個新商品",
                            contents = new
                            {
                                type = "carousel",
                                contents = bubbles
                            }
                        }
                    }
                };

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var response = await client.PostAsJsonAsync("https://api.line.me/v2/bot/message/broadcast", payload);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("LINE broadcast 成功：{body}", responseBody);
                }
                else
                {
                    _logger.LogError("LINE broadcast 失敗 (status={status})：{body}", response.StatusCode, responseBody);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LINE broadcast 發送失敗");
            }
        }
    }
}
