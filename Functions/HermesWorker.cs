using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HermesProductParserFunc.Functions
{
    public class HermesWorker : BackgroundService
    {
        private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

        private readonly HermesScraper _scraper;
        private readonly ILogger<HermesWorker> _logger;
        private readonly TimeSpan _interval;

        public HermesWorker(HermesScraper scraper, ILogger<HermesWorker> logger)
        {
            _scraper = scraper;
            _logger = logger;
            _interval = ResolveInterval();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Hermes worker started. Interval: {intervalSeconds} seconds", _interval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _scraper.RunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error while running Hermes scrape cycle");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            _logger.LogInformation("Hermes worker stopped.");
        }

        private static TimeSpan ResolveInterval()
        {
            var configuredValue = Environment.GetEnvironmentVariable("SCRAPE_INTERVAL_SECONDS");
            if (int.TryParse(configuredValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return DefaultInterval;
        }
    }
}