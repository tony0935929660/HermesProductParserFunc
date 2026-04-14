using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
        public string ProductUrl { get; set; }
        public string Color { get; set; }
    }

    public class HermesScraper
    {
        private const string HermesHomeUrl = "https://www.hermes.com/tw/zh/";
        private const string HermesUrl = "https://www.hermes.com/tw/zh/category/leather-goods/bags-and-clutches/#|";
        private const string DefaultUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly string[] ConsentSelectors =
        {
            "#onetrust-accept-btn-handler",
            "button[data-testid='uc-accept-all-button']",
            "button[id*='accept']",
            "button[aria-label*='Accept']",
            "button[title*='Accept']"
        };

        private readonly ILogger<HermesScraper> _logger;

        public HermesScraper(ILogger<HermesScraper> logger)
        {
            _logger = logger;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
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
                Url = HermesUrl
            };
            var runFileLogger = new ScrapeRunFileLogger();
            string htmlSnapshot = null;

            _logger.LogInformation("Repository mode: {repositoryMode}", repositoryMode);

            try
            {
                repo.InitDb();

                _logger.LogInformation("Hermes worker cycle started at: {runStartedAtUtc}", runStartedAtUtc);

                var options = new ChromeOptions();
                var chromeArguments = new List<string>
                {
                    "--headless=new",
                    "--no-sandbox",
                    "--disable-gpu",
                    "--disable-dev-shm-usage",
                    "--disable-extensions",
                    "--disable-software-rasterizer",
                    "--disable-setuid-sandbox",
                    "--window-size=1920,1080",
                    "--start-maximized",
                    "--lang=zh-TW",
                    "--disable-blink-features=AutomationControlled"
                };
                foreach (var argument in chromeArguments)
                {
                    options.AddArgument(argument);
                }

                options.AddExcludedArgument("enable-automation");
                options.AddAdditionalOption("useAutomationExtension", false);

                var chromePath = ResolveChromeBinaryPath();
                var chromePathExists = !string.IsNullOrWhiteSpace(chromePath) && System.IO.File.Exists(chromePath);
                if (chromePathExists)
                {
                    options.BinaryLocation = chromePath;
                }

                var chromeDriverPath = ResolveChromeDriverPath();
                var service = !string.IsNullOrWhiteSpace(chromeDriverPath)
                    ? ChromeDriverService.CreateDefaultService(Path.GetDirectoryName(chromeDriverPath), Path.GetFileName(chromeDriverPath))
                    : ChromeDriverService.CreateDefaultService();
                service.SuppressInitialDiagnosticInformation = true;
                service.HideCommandPromptWindow = true;

                _logger.LogInformation("ChromeDriver binary: {chromeDriverBinary}", string.IsNullOrWhiteSpace(chromeDriverPath) ? "<default>" : chromeDriverPath);

                runRecord.RuntimeInfo = new RuntimeInfoRecord
                {
                    HostName = Environment.MachineName,
                    OsDescription = RuntimeInformation.OSDescription,
                    OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    BaseDirectory = AppContext.BaseDirectory,
                    CurrentDirectory = Environment.CurrentDirectory,
                    ScriptRoot = Environment.GetEnvironmentVariable("AzureWebJobsScriptRoot"),
                    ChromePathExists = chromePathExists,
                    ChromeBinary = string.IsNullOrWhiteSpace(options.BinaryLocation) ? "<default>" : options.BinaryLocation,
                    ChromeArguments = chromeArguments
                };

                _logger.LogInformation("Chrome runtime info: Host={host}, OS={os}, Arch={arch}, ProcessArch={processArch}, BaseDir={baseDir}, CurrentDir={currentDir}, ChromePathExists={chromePathExists}, ChromeBinary={chromeBinary}",
                    runRecord.RuntimeInfo.HostName,
                    runRecord.RuntimeInfo.OsDescription,
                    runRecord.RuntimeInfo.OsArchitecture,
                    runRecord.RuntimeInfo.ProcessArchitecture,
                    runRecord.RuntimeInfo.BaseDirectory,
                    runRecord.RuntimeInfo.CurrentDirectory,
                    runRecord.RuntimeInfo.ChromePathExists,
                    runRecord.RuntimeInfo.ChromeBinary);

                ChromeDriver driver;
                try
                {
                    driver = new ChromeDriver(service, options, TimeSpan.FromSeconds(60));
                    runRecord.BrowserVersion = GetBrowserVersion(driver);
                    runRecord.ChromeDriverVersion = GetChromeDriverVersion(driver);
                    _logger.LogInformation("Chrome capabilities: BrowserVersion={browserVersion}, ChromeDriverVersion={chromeDriverVersion}",
                        runRecord.BrowserVersion ?? "unknown",
                        runRecord.ChromeDriverVersion ?? "unknown");
                }
                catch (Exception ex)
                {
                    ApplyFailure(runRecord, "ChromeDriver 初始化失敗", ex);
                    _logger.LogError(ex, "ChromeDriver 初始化失敗");
                    throw;
                }

                using (driver)
                {
                    try
                    {
                        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);
                        driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
                        TryApplyStealthSettings(driver, runRecord.BrowserVersion);
                        await NavigateWithWarmupAsync(driver, cancellationToken);

                        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));

                        var readyStateStopwatch = Stopwatch.StartNew();
                        wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").ToString() == "complete");
                        readyStateStopwatch.Stop();
                        runRecord.ReadyStateElapsedMs = readyStateStopwatch.ElapsedMilliseconds;

                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        runRecord.ConsentSelector = TryAcceptConsent(driver);

                        var pageSignalStopwatch = Stopwatch.StartNew();
                        var pageSignal = WaitForPageSignal(driver, TimeSpan.FromSeconds(30));
                        pageSignalStopwatch.Stop();

                        runRecord.PageSignal = pageSignal;
                        runRecord.PageSignalElapsedMs = pageSignalStopwatch.ElapsedMilliseconds;
                        runRecord.CurrentPageUrl = SafeGetCurrentUrl(driver);
                        runRecord.NavigatorFingerprint = CaptureNavigatorFingerprint(driver);
                        runRecord.SelectorCounts = CaptureSelectorCounts(driver);
                        runRecord.StorageSummary = CaptureStorageSummary(driver);
                        runRecord.MainDocumentMetadata = await CaptureMainDocumentMetadataAsync(driver);

                        _logger.LogInformation("[DEBUG] After readyState, driver.Url: {url}, signal: {signal}, title: {title}, readyStateElapsedMs: {readyStateElapsedMs}, pageSignalElapsedMs: {pageSignalElapsedMs}",
                            runRecord.CurrentPageUrl,
                            pageSignal,
                            driver.Title,
                            runRecord.ReadyStateElapsedMs,
                            runRecord.PageSignalElapsedMs);
                        _logger.LogInformation("[DEBUG] NavigatorFingerprint: {fingerprint}", JsonSerializer.Serialize(runRecord.NavigatorFingerprint));
                        _logger.LogInformation("[DEBUG] SelectorCounts: {selectorCounts}", JsonSerializer.Serialize(runRecord.SelectorCounts));
                        _logger.LogInformation("[DEBUG] StorageSummary: {storageSummary}", JsonSerializer.Serialize(runRecord.StorageSummary));
                        _logger.LogInformation("[DEBUG] MainDocumentMetadata: {mainDocumentMetadata}", JsonSerializer.Serialize(runRecord.MainDocumentMetadata));

                        var ps1 = SafeGetPageSource(driver);
                        if (!string.IsNullOrEmpty(ps1))
                        {
                            _logger.LogInformation($"[DEBUG] PageSource前2000字: {ps1.Substring(0, Math.Min(2000, ps1.Length))}");
                        }

                        var bodyText = SafeGetVisibleBodyText(driver);
                        if (!string.IsNullOrWhiteSpace(bodyText))
                        {
                            _logger.LogInformation("[DEBUG] BodyText前1000字: {bodyText}", bodyText.Substring(0, Math.Min(1000, bodyText.Length)));
                        }

                        runRecord.BlockSignals = CollectBlockSignals(driver, ps1, bodyText);
                        runRecord.BlockReason = string.Join("; ", runRecord.BlockSignals);
                        if (runRecord.BlockSignals.Count > 0)
                        {
                            _logger.LogWarning("[DEBUG] Block signals detected: {blockSignals}", string.Join(" | ", runRecord.BlockSignals));
                        }

                        int expectedCount = 0;
                        try
                        {
                            var headerElem = wait.Until(drv => drv.FindElements(By.CssSelector(".header-title-current-number-result")).FirstOrDefault(elem => elem.Displayed));
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
                            var blockReason = DetectBlockReason(driver, ps1, bodyText);
                            runRecord.Outcome = "UnexpectedPageState";
                            runRecord.Message = string.IsNullOrWhiteSpace(blockReason)
                                ? "預期商品數量為 0，可能無法正確爬取"
                                : $"預期商品數量為 0，可能遭遇阻擋或頁面異常: {blockReason}";
                            runRecord.BlockReason = blockReason;
                            runRecord.CurrentPageUrl = SafeGetCurrentUrl(driver);
                            htmlSnapshot = SafeGetPageSource(driver);
                            CaptureScreenshotIfMissing(driver, runRecord);
                            _logger.LogWarning("[DEBUG] 預期商品數量為 0，可能無法正確爬取。判斷: {blockReason}", blockReason ?? "unknown");
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
                            runRecord.CurrentPageUrl = SafeGetCurrentUrl(driver);
                            htmlSnapshot = SafeGetPageSource(driver);
                            CaptureScreenshotIfMissing(driver, runRecord);
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
                                CaptureScreenshotIfMissing(driver, runRecord);
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

                                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
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

                                string productUrl = null;
                                try
                                {
                                    var productLink = productElement
                                        .FindElements(By.CssSelector("a[href]"))
                                        .Select(link => ResolveHermesUrl(link.GetAttribute("href")))
                                        .FirstOrDefault(link => !string.IsNullOrWhiteSpace(link) && link.Contains("/product/", StringComparison.OrdinalIgnoreCase));

                                    if (string.IsNullOrWhiteSpace(productLink))
                                    {
                                        productLink = productElement
                                            .FindElements(By.CssSelector("a[href]"))
                                            .Select(link => ResolveHermesUrl(link.GetAttribute("href")))
                                            .FirstOrDefault(link => !string.IsNullOrWhiteSpace(link));
                                    }

                                    if (!string.IsNullOrWhiteSpace(productLink))
                                    {
                                        productUrl = productLink;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning($"查找商品連結失敗: {ex.Message}");
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
                                        id = ExtractProductId(productUrl, imageUrl, title, price, color);
                                        if (id.StartsWith("fallback-", StringComparison.Ordinal))
                                        {
                                            _logger.LogWarning("id parse fallback used for imageUrl: {imageUrl}", imageUrl);
                                        }
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
                                        ProductUrl = productUrl,
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
                            runRecord.CurrentPageUrl = SafeGetCurrentUrl(driver);

                            if (products.Count > 0)
                            {
                                var oldList = repo.GetAllProducts();
                                var newProducts = products
                                    .Where(n => !oldList.Any(o => SameProductIdentity(o, n)))
                                    .ToList();

                                var deletedProducts = oldList
                                    .Where(o => !products.Any(n => SameProductIdentity(o, n)))
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
                                    runRecord.BroadcastCount = await BroadcastLineMessageAsync(newProducts);
                                }
                                else
                                {
                                    _logger.LogInformation("LINE broadcast skipped: no new products in this cycle.");
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
                                CaptureScreenshotIfMissing(driver, runRecord);
                                _logger.LogWarning("未爬取到任何商品");
                            }
                        }
                        catch (Exception ex)
                        {
                            runRecord.CurrentPageUrl = SafeGetCurrentUrl(driver);
                            htmlSnapshot ??= SafeGetPageSource(driver);
                            CaptureScreenshotIfMissing(driver, runRecord);
                            ApplyFailure(runRecord, "爬取商品列表失敗", ex);
                            _logger.LogError(ex, "爬取商品列表失敗");
                        }
                    }
                    catch
                    {
                        runRecord.CurrentPageUrl ??= SafeGetCurrentUrl(driver);
                        htmlSnapshot ??= SafeGetPageSource(driver);
                        CaptureScreenshotIfMissing(driver, runRecord);
                        throw;
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

                if (!string.IsNullOrWhiteSpace(runRecord.ScreenshotPath))
                {
                    _logger.LogInformation("Screenshot 已寫入: {screenshotPath}", runRecord.ScreenshotPath);
                }

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

        private static string SafeGetCurrentUrl(IWebDriver driver)
        {
            try
            {
                return driver.Url;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveHermesUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.Scheme is "http" or "https"
                    ? absoluteUri.AbsoluteUri
                    : null;
            }

            if (!Uri.TryCreate(new Uri(HermesHomeUrl), url, out var resolvedUri))
            {
                return null;
            }

            return resolvedUri.Scheme is "http" or "https"
                ? resolvedUri.AbsoluteUri
                : null;
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

        private static string ResolveChromeDriverPath()
        {
            var configuredChromeDriverPath = Environment.GetEnvironmentVariable("CHROMEDRIVER_PATH");
            if (!string.IsNullOrWhiteSpace(configuredChromeDriverPath) && System.IO.File.Exists(configuredChromeDriverPath))
            {
                return configuredChromeDriverPath;
            }

            var candidatePaths = new[]
            {
                "/usr/local/bin/chromedriver",
                "/usr/bin/chromedriver",
                "C:\\chromedriver-win64\\chromedriver.exe"
            };

            return candidatePaths.FirstOrDefault(System.IO.File.Exists);
        }

        private static void TryApplyStealthSettings(ChromeDriver driver, string browserVersion)
        {
            try
            {
                var resolvedUserAgent = BuildUserAgent(browserVersion);
                driver.ExecuteCdpCommand("Network.enable", new Dictionary<string, object>());
                driver.ExecuteCdpCommand("Network.setUserAgentOverride", new Dictionary<string, object>
                {
                    ["userAgent"] = resolvedUserAgent,
                    ["acceptLanguage"] = "zh-TW,zh;q=0.9,en-US;q=0.8,en;q=0.7",
                    ["platform"] = "Linux x86_64"
                });
                driver.ExecuteCdpCommand("Network.setExtraHTTPHeaders", new Dictionary<string, object>
                {
                    ["headers"] = new Dictionary<string, object>
                    {
                        ["Accept-Language"] = "zh-TW,zh;q=0.9,en-US;q=0.8,en;q=0.7"
                    }
                });
                driver.ExecuteCdpCommand("Emulation.setTimezoneOverride", new Dictionary<string, object>
                {
                    ["timezoneId"] = "Asia/Taipei"
                });
                driver.ExecuteCdpCommand("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
                {
                    ["width"] = 1920,
                    ["height"] = 1080,
                    ["deviceScaleFactor"] = 1,
                    ["mobile"] = false,
                    ["screenWidth"] = 1920,
                    ["screenHeight"] = 1080,
                    ["positionX"] = 0,
                    ["positionY"] = 0
                });

                driver.ExecuteCdpCommand("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object>
                {
                    ["source"] = @"
Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
Object.defineProperty(navigator, 'languages', { get: () => ['zh-TW', 'zh', 'en-US', 'en'] });
Object.defineProperty(navigator, 'platform', { get: () => 'Linux x86_64' });
Object.defineProperty(navigator, 'language', { get: () => 'zh-TW' });
Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 8 });
Object.defineProperty(navigator, 'deviceMemory', { get: () => 8 });
Object.defineProperty(navigator, 'vendor', { get: () => 'Google Inc.' });
Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
window.chrome = { app: {}, runtime: {}, webstore: {} };
"
                });
            }
            catch
            {
            }
        }

        private static async Task NavigateWithWarmupAsync(IWebDriver driver, CancellationToken cancellationToken)
        {
            driver.Navigate().GoToUrl(HermesHomeUrl);
            WaitForDocumentReady(driver, TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);

            driver.Navigate().GoToUrl(HermesUrl);
        }

        private static void WaitForDocumentReady(IWebDriver driver, TimeSpan timeout)
        {
            var wait = new WebDriverWait(driver, timeout);
            wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState")?.ToString() == "complete");
        }

        private string TryAcceptConsent(IWebDriver driver)
        {
            foreach (var selector in ConsentSelectors)
            {
                try
                {
                    var button = driver.FindElements(By.CssSelector(selector)).FirstOrDefault(element => element.Displayed && element.Enabled);
                    if (button == null)
                    {
                        continue;
                    }

                    ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", button);
                    _logger.LogInformation("[DEBUG] Clicked consent button: {selector}", selector);
                    return selector;
                }
                catch
                {
                }
            }

            return null;
        }

        private static string WaitForPageSignal(IWebDriver driver, TimeSpan timeout)
        {
            var wait = new WebDriverWait(driver, timeout);
            return wait.Until(drv =>
            {
                if (drv.FindElements(By.CssSelector(".header-title-current-number-result")).Any())
                {
                    return "header-count";
                }

                if (drv.FindElements(By.CssSelector(".product-item")).Any())
                {
                    return "product-items";
                }

                var title = drv.Title ?? string.Empty;
                var pageSource = SafeGetPageSource(drv) ?? string.Empty;
                if (ContainsBlockMarkers(title) || ContainsBlockMarkers(pageSource))
                {
                    return "blocked";
                }

                return null;
            });
        }

        private static string SafeGetVisibleBodyText(IWebDriver driver)
        {
            try
            {
                var body = driver.FindElements(By.TagName("body")).FirstOrDefault();
                return body?.Text;
            }
            catch
            {
                return null;
            }
        }

        private static NavigatorFingerprintRecord CaptureNavigatorFingerprint(IWebDriver driver)
        {
            try
            {
                var json = (string)((IJavaScriptExecutor)driver).ExecuteScript(@"return JSON.stringify({
    userAgent: navigator.userAgent,
    webdriver: navigator.webdriver,
    platform: navigator.platform,
    languages: navigator.languages,
    language: navigator.language,
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    screenWidth: window.screen.width,
    screenHeight: window.screen.height,
    devicePixelRatio: window.devicePixelRatio
});");

                return string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<NavigatorFingerprintRecord>(json, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static SelectorCountsRecord CaptureSelectorCounts(IWebDriver driver)
        {
            try
            {
                return new SelectorCountsRecord
                {
                    HeaderCount = driver.FindElements(By.CssSelector(".header-title-current-number-result")).Count,
                    ProductItemCount = driver.FindElements(By.CssSelector(".product-item")).Count,
                    ProductTitleCount = driver.FindElements(By.CssSelector(".product-title")).Count,
                    ProductPriceCount = driver.FindElements(By.CssSelector(".product-item-price, .price.small")).Count,
                    ProductColorCount = driver.FindElements(By.CssSelector(".product-item-colors")).Count,
                    CaptchaIframeCount = driver.FindElements(By.CssSelector("iframe[src*='captcha'], iframe[title*='captcha']")).Count,
                    ChallengeIframeCount = driver.FindElements(By.CssSelector("iframe[src*='challenge'], iframe[title*='challenge']")).Count,
                    ConsentButtonPresent = ConsentSelectors.Any(selector => driver.FindElements(By.CssSelector(selector)).Any(element => element.Displayed))
                };
            }
            catch
            {
                return null;
            }
        }

        private static StorageSummaryRecord CaptureStorageSummary(IWebDriver driver)
        {
            try
            {
                var json = (string)((IJavaScriptExecutor)driver).ExecuteScript(@"return JSON.stringify({
    cookieCount: document.cookie ? document.cookie.split(';').filter(Boolean).length : 0,
    localStorageCount: window.localStorage ? window.localStorage.length : 0,
    sessionStorageCount: window.sessionStorage ? window.sessionStorage.length : 0
});");

                return string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<StorageSummaryRecord>(json, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<MainDocumentMetadataRecord> CaptureMainDocumentMetadataAsync(IWebDriver driver)
        {
            try
            {
                var json = (string)((IJavaScriptExecutor)driver).ExecuteAsyncScript(@"
const done = arguments[arguments.length - 1];
fetch(window.location.href, { method: 'HEAD', credentials: 'include', cache: 'no-store' })
    .then(response => done(JSON.stringify({
        statusCode: response.status,
        contentType: response.headers.get('content-type'),
        server: response.headers.get('server'),
        location: response.headers.get('location'),
        cacheControl: response.headers.get('cache-control'),
        cfRay: response.headers.get('cf-ray'),
        xCache: response.headers.get('x-cache'),
        fetchError: null
    })))
    .catch(error => done(JSON.stringify({
        statusCode: null,
        contentType: null,
        server: null,
        location: null,
        cacheControl: null,
        cfRay: null,
        xCache: null,
        fetchError: String(error)
    })));");

                return string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<MainDocumentMetadataRecord>(json, JsonOptions);
            }
            catch
            {
                return await Task.FromResult<MainDocumentMetadataRecord>(null);
            }
        }

        private static void CaptureScreenshotIfMissing(IWebDriver driver, ScrapeRunRecord runRecord)
        {
            if (!string.IsNullOrWhiteSpace(runRecord.ScreenshotPath))
            {
                return;
            }

            try
            {
                if (driver is not ITakesScreenshot screenshotDriver)
                {
                    return;
                }

                var screenshotDirectory = ScrapeRunFileLogger.ResolveArtifactDirectory(runRecord.StartedAtUtc, "screenshots");
                var screenshotPath = Path.Combine(screenshotDirectory, $"{runRecord.StartedAtUtc:yyyyMMdd-HHmmss}-{runRecord.RunId}.png");
                screenshotDriver.GetScreenshot().SaveAsFile(screenshotPath);
                runRecord.ScreenshotPath = screenshotPath;
            }
            catch
            {
            }
        }

        private static string GetBrowserVersion(ChromeDriver driver)
        {
            try
            {
                return driver.Capabilities.GetCapability("browserVersion")?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string GetChromeDriverVersion(ChromeDriver driver)
        {
            try
            {
                var chromeCapability = driver.Capabilities.GetCapability("chrome");
                if (chromeCapability is IDictionary chromeDictionary && chromeDictionary.Contains("chromedriverVersion"))
                {
                    return chromeDictionary["chromedriverVersion"]?.ToString()?.Split(' ').FirstOrDefault();
                }
            }
            catch
            {
            }

            return null;
        }

        private static string BuildUserAgent(string browserVersion)
        {
            if (string.IsNullOrWhiteSpace(browserVersion))
            {
                return DefaultUserAgent;
            }

            return Regex.Replace(DefaultUserAgent, @"Chrome/[\d.]+", $"Chrome/{browserVersion}");
        }

        private static string ExtractProductId(string productUrl, string imageUrl, string title, string price, string color)
        {
            if (!string.IsNullOrWhiteSpace(productUrl))
            {
                var match = Regex.Match(productUrl, @"/product/[^/]+-H?([A-Z0-9]+)/?", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value.ToUpperInvariant();
                }
            }

            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                var match = Regex.Match(imageUrl, @"hermesproduct/([^/?]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var rawAssetName = match.Groups[1].Value;
                    var normalizedId = rawAssetName.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(normalizedId))
                    {
                        return normalizedId;
                    }
                }
            }

            var fallbackSource = string.Join("|", new[]
            {
                title ?? string.Empty,
                price ?? string.Empty,
                color ?? string.Empty,
                imageUrl ?? string.Empty
            });
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(fallbackSource));
            return $"fallback-{Convert.ToHexString(hashBytes).ToLowerInvariant()[..16]}";
        }

        private static bool SameProductIdentity(Product left, Product right)
        {
            if (!string.IsNullOrWhiteSpace(left?.Id) && !string.IsNullOrWhiteSpace(right?.Id))
            {
                return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(left?.Title, right?.Title, StringComparison.Ordinal)
                && string.Equals(left?.Price, right?.Price, StringComparison.Ordinal)
                && string.Equals(left?.Color ?? string.Empty, right?.Color ?? string.Empty, StringComparison.Ordinal);
        }

        private static List<string> CollectBlockSignals(IWebDriver driver, string pageSource, string bodyText)
        {
            var blockSignals = new List<string>();

            try
            {
                if (ContainsBlockMarkers(driver.Title))
                {
                    blockSignals.Add($"title:{driver.Title}");
                }

                if (ContainsBlockMarkers(bodyText))
                {
                    blockSignals.Add("body:block-markers");
                }

                if (ContainsBlockMarkers(pageSource))
                {
                    blockSignals.Add("html:block-markers");
                }

                var captchaIframeCount = driver.FindElements(By.CssSelector("iframe[src*='captcha'], iframe[title*='captcha']")).Count;
                if (captchaIframeCount > 0)
                {
                    blockSignals.Add($"captcha-iframe:{captchaIframeCount}");
                }

                var challengeIframeCount = driver.FindElements(By.CssSelector("iframe[src*='challenge'], iframe[title*='challenge']")).Count;
                if (challengeIframeCount > 0)
                {
                    blockSignals.Add($"challenge-iframe:{challengeIframeCount}");
                }
            }
            catch
            {
            }

            return blockSignals;
        }

        private static string DetectBlockReason(IWebDriver driver, string pageSource, string bodyText)
        {
            var blockSignals = CollectBlockSignals(driver, pageSource, bodyText);
            return blockSignals.Count == 0 ? null : string.Join("; ", blockSignals);
        }

        private static bool ContainsBlockMarkers(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.ToLowerInvariant();
            return normalized.Contains("access denied")
                || normalized.Contains("forbidden")
                || normalized.Contains("verify you are human")
                || normalized.Contains("captcha")
                || normalized.Contains("robot")
                || normalized.Contains("blocked")
                || normalized.Contains("request unsuccessful")
                || normalized.Contains("temporary unavailable");
        }

        private async Task<int> BroadcastLineMessageAsync(List<Product> products)
        {
            var token = Environment.GetEnvironmentVariable("LINE_BOT_CHANNEL_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("環境變數 LINE_BOT_CHANNEL_ACCESS_TOKEN 未設定，無法使用 LINE Messaging API 廣播。");
                return 0;
            }

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var sentProductCount = 0;
                var productBatches = products.Chunk(12).ToArray();
                for (var batchIndex = 0; batchIndex < productBatches.Length; batchIndex++)
                {
                    var batch = productBatches[batchIndex];
                    var bubbles = batch.Select(p =>
                    {
                        var lineTargetUrl = ResolveHermesUrl(p.ProductUrl) ?? HermesUrl;
                        return new
                        {
                            type = "bubble",
                            hero = new
                            {
                                type = "image",
                                size = "full",
                                aspectRatio = "1:1",
                                aspectMode = "cover",
                                url = p.ImageUrl,
                                action = new
                                {
                                    type = "uri",
                                    uri = lineTargetUrl
                                }
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
                        };
                    }).ToList();

                    var payload = new
                    {
                        messages = new[]
                        {
                            new
                            {
                                type = "flex",
                                altText = $"Hermes 皮件商品通知 ({batchIndex + 1}/{productBatches.Length}) - 本批 {batch.Length} 個商品",
                                contents = new
                                {
                                    type = "carousel",
                                    contents = bubbles
                                }
                            }
                        }
                    };

                    var response = await client.PostAsJsonAsync("https://api.line.me/v2/bot/message/broadcast", payload);
                    var responseBody = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        sentProductCount += batch.Length;
                        _logger.LogInformation("LINE broadcast 成功 (batch={batchIndex}/{batchCount}, size={batchSize})：{body}", batchIndex + 1, productBatches.Length, batch.Length, responseBody);
                    }
                    else
                    {
                        _logger.LogError("LINE broadcast 失敗 (batch={batchIndex}/{batchCount}, size={batchSize}, status={status})：{body}", batchIndex + 1, productBatches.Length, batch.Length, response.StatusCode, responseBody);
                    }
                }

                return sentProductCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LINE broadcast 發送失敗");
                return 0;
            }
        }
    }
}
