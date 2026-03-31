using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        public string Title { get; set; }
        public string Price { get; set; }
        public string ImageUrl { get; set; }
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
            if (Environment.GetEnvironmentVariable("USE_AZURE_SQL") == "1")
                repo = new AzureSqlProductRepository(Environment.GetEnvironmentVariable("AZURE_SQL_CONN"));
            else
                repo = new SqliteProductRepository();

            bool needInitDb = Environment.GetEnvironmentVariable("INIT_DB") == "1";
            if (needInitDb)
            {
                repo.InitDb();
            }

            _logger.LogInformation($"HermesTimer executed at: {DateTime.UtcNow:O}");

            var url = "https://www.hermes.com/tw/zh/category/leather-goods/bags-and-clutches/#|";

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

            var chromePath = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
            if (System.IO.File.Exists(chromePath))
            {
                options.BinaryLocation = chromePath;
            }

            var service = ChromeDriverService.CreateDefaultService();
            service.SuppressInitialDiagnosticInformation = true;
            service.HideCommandPromptWindow = true;

            try
            {
                using var driver = new ChromeDriver(service, options, TimeSpan.FromSeconds(60));
                driver.Navigate().GoToUrl(url);

                var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(60));

                // 1. 等待 document.readyState 完成
                wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").ToString() == "complete");

                // 3. 再等待商品元素出現（保險）
                wait.Until(SeleniumExtras.WaitHelpers.ExpectedConditions.ElementExists(By.CssSelector(".product-item")));

                // 爬取所有商品信息
                var products = new List<Product>();
                try
                {
                    var productElements = driver.FindElements(By.CssSelector(".product-item"));
                    _logger.LogInformation($"找到 {productElements.Count} 個商品");
                    if (productElements.Count == 0)
                    {
                        var pageSource = driver.PageSource;
                        _logger.LogWarning($"PageSource前2000字: {pageSource.Substring(0, Math.Min(2000, pageSource.Length))}");
                    }

                    foreach (var productElement in productElements)
                    {
                        try
                        {
                            _logger.LogInformation("開始爬取一個商品元素");

                            // 在同一個 .product-item 容器內查找所有信息
                            var titleElement = productElement.FindElement(By.CssSelector(".product-title, .product-item-name .product-title"));
                            // 修正 price selector，正確抓取價格
                            var priceElement = productElement.FindElement(By.CssSelector(".product-item-price .price.small"));
                            
                            var title = titleElement?.Text?.Trim();
                            var price = priceElement?.Text?.Trim();

                            _logger.LogInformation($"找到 - Title: '{title}', Price: '{price}'");

                            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(price))
                            {
                                string outerHtml = string.Empty;
                                try
                                {
                                    outerHtml = productElement.GetAttribute("outerHTML");
                                }
                                catch { }
                                _logger.LogWarning($"商品信息不完整，跳過 - Title: '{title}', Price: '{price}'，outerHTML: {outerHtml}");
                                continue;
                            }

                            string imageUrl = null;
                            try
                            {
                                var images = productElement.FindElements(By.TagName("img"));
                                _logger.LogInformation($"找到 {images.Count} 個 img 元素");
                                foreach (var img in images)
                                {
                                    var dataSrc = img.GetAttribute("data-src");
                                    var src = img.GetAttribute("src");
                                    if (!string.IsNullOrEmpty(dataSrc))
                                    {
                                        imageUrl = dataSrc;
                                        _logger.LogInformation($"取得 data-src: {dataSrc}");
                                        break;
                                    }
                                    else if (!string.IsNullOrEmpty(src) && !src.Contains("base64") && !src.Contains("placeholder"))
                                    {
                                        imageUrl = src;
                                        _logger.LogInformation($"取得 src: {src}");
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
                                var product = new Product
                                {
                                    Title = title,
                                    Price = price,
                                    ImageUrl = imageUrl
                                };

                                products.Add(product);
                                _logger.LogInformation($"✓ 爬取成功: {product.Title} - {product.Price}");
                            }
                            else
                            {
                                _logger.LogWarning($"✗ 商品資料不完整: Title={!string.IsNullOrEmpty(title)}, Price={!string.IsNullOrEmpty(price)}, ImageUrl={!string.IsNullOrEmpty(imageUrl)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"爬取單個商品失敗: {ex.Message}");
                            _logger.LogInformation($"例外詳情: {ex}");
                        }
                    }

                    if (products.Count > 0)
                    {
                        _logger.LogInformation($"成功爬取 {products.Count} 個商品");
                        await BroadcastLineMessageAsync(products);
                    }
                    else
                    {
                        _logger.LogWarning("未爬取到任何商品");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "爬取商品列表失敗");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "抓取失敗");
            }
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
                            altText = $"Hermes 皮件商品 - 共 {products.Count} 個商品",
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
