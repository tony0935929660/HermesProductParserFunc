using System;
using System.Collections.Generic;
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
                wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").ToString() == "complete");
                System.Threading.Thread.Sleep(3000); // give JS a bit more time

                IWebElement element = null;
                try
                {
                    element = wait.Until(d => d.FindElement(By.XPath("//span[@data-testid='number-current-result']")));
                }
                catch (WebDriverTimeoutException)
                {
                    try
                    {
                        element = wait.Until(d => d.FindElement(By.CssSelector("span.header-title-current-number-result.ng-star-inserted")));
                    }
                    catch (WebDriverTimeoutException)
                    {
                        _logger.LogWarning("沒找到指定目標元素，將輸出少量 page source 供診斷（後續可關閉）。");
                        var html = driver.PageSource;
                        var snippet = html.Length > 3000 ? html.Substring(0, 3000) : html;
                        _logger.LogInformation("pageSource snippet: {snippet}", snippet.Replace("\r\n", " ").Replace("\n", " "));
                        _logger.LogWarning("目標元素未找到，這通常表示網頁被防機器人封鎖或 DOM 結構不符。保持這個訊息以便排查。\n原本已抓取訊息可能無法輸出是因為流程卡在這裡。");
                        return;
                    }
                }

                var text = element?.Text.Trim();

                _logger.LogInformation("找到元素: {elementText}", text);

                if (string.IsNullOrEmpty(text))
                {
                    _logger.LogWarning("找到元素但內容為空。可能需要等待更多 JS 渲染（或需要另個 selector）。");
                }

                var match = Regex.Match(text ?? string.Empty, @"\((\d+)\)");
                var number = match.Success ? match.Groups[1].Value : text ?? string.Empty;

                if (string.IsNullOrEmpty(number))
                {
                    _logger.LogWarning("未解析出數字，原始 elementText: {text}", text);
                }
                else
                {
                    _logger.LogInformation("抓取到數字：{number}", number);
                    await BroadcastLineMessageAsync(number);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "抓取失敗");
            }
        }

        private async Task BroadcastLineMessageAsync(string number)
        {
            var token = Environment.GetEnvironmentVariable("LINE_BOT_CHANNEL_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("環境變數 LINE_BOT_CHANNEL_ACCESS_TOKEN 未設定，無法使用 LINE Messaging API 廣播。");
                return;
            }

            var payload = new
            {
                messages = new[]
                {
                    new { type = "text", text = $"目前皮件數量是 {number} 個" }
                }
            };

            try
            {
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
