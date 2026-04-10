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
        private readonly IHostApplicationLifetime _applicationLifetime;
        private readonly ILogger<HermesWorker> _logger;
        private readonly TimeSpan _interval;
        private readonly bool _runOnce;

        public HermesWorker(HermesScraper scraper, IHostApplicationLifetime applicationLifetime, ILogger<HermesWorker> logger)
        {
            _scraper = scraper;
            _applicationLifetime = applicationLifetime;
            _logger = logger;
            _interval = ResolveInterval();
            _runOnce = ResolveRunOnce();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Hermes worker started. Interval: {intervalSeconds} seconds, RunOnce: {runOnce}", _interval.TotalSeconds, _runOnce);

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

                if (_runOnce)
                {
                    _logger.LogInformation("Run-once mode enabled. Stopping host after the first scrape cycle.");
                    _applicationLifetime.StopApplication();
                    break;
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

        private static bool ResolveRunOnce()
        {
            var configuredValue = Environment.GetEnvironmentVariable("RUN_ONCE");
            return string.Equals(configuredValue, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(configuredValue, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}