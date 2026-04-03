using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace HermesProductParserFunc.Functions
{
    public class ScrapeRunRecord
    {
        public string RunId { get; set; }
        public string Outcome { get; set; }
        public string Message { get; set; }
        public string RepositoryMode { get; set; }
        public string Url { get; set; }
        public string CurrentPageUrl { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime CompletedAtUtc { get; set; }
        public long DurationMs { get; set; }
        public int? ExpectedCount { get; set; }
        public int? ScrapedCount { get; set; }
        public int? PersistedCount { get; set; }
        public int? NewProductsCount { get; set; }
        public int? DeletedProductsCount { get; set; }
        public int? BroadcastCount { get; set; }
        public string ErrorType { get; set; }
        public string ErrorMessage { get; set; }
        public string HtmlSnapshotPath { get; set; }
        public string LogFilePath { get; set; }
    }

    public class ScrapeRunFileLogger
    {
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public async Task PersistAsync(ScrapeRunRecord record, string htmlSnapshot)
        {
            var logFilePath = ResolveLogFilePath(record.StartedAtUtc);
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));

            record.LogFilePath = logFilePath;
            if (!string.IsNullOrWhiteSpace(htmlSnapshot))
            {
                record.HtmlSnapshotPath = await WriteSnapshotAsync(record, htmlSnapshot, logFilePath);
            }

            var jsonLine = JsonSerializer.Serialize(record, _jsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(logFilePath, jsonLine);
        }

        private static string ResolveLogFilePath(DateTime startedAtUtc)
        {
            var configuredFilePath = Environment.GetEnvironmentVariable("SCRAPE_RUN_LOG_PATH");
            if (!string.IsNullOrWhiteSpace(configuredFilePath))
            {
                return AppPathResolver.ResolvePath(configuredFilePath);
            }

            var configuredDirectory = Environment.GetEnvironmentVariable("SCRAPE_RUN_LOG_DIR");
            var logDirectory = AppPathResolver.ResolvePath(configuredDirectory, "data", "scrape-runs");
            return Path.Combine(logDirectory, $"hermes-scrape-{startedAtUtc:yyyyMMdd}.jsonl");
        }

        private static async Task<string> WriteSnapshotAsync(ScrapeRunRecord record, string htmlSnapshot, string logFilePath)
        {
            var snapshotDirectory = Path.Combine(Path.GetDirectoryName(logFilePath), "snapshots");
            Directory.CreateDirectory(snapshotDirectory);

            var snapshotPath = Path.Combine(snapshotDirectory, $"{record.StartedAtUtc:yyyyMMdd-HHmmss}-{record.RunId}.html");
            await File.WriteAllTextAsync(snapshotPath, htmlSnapshot);
            return snapshotPath;
        }
    }
}