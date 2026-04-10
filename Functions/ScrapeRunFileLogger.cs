using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace HermesProductParserFunc.Functions
{
    public class RuntimeInfoRecord
    {
        public string HostName { get; set; }
        public string OsDescription { get; set; }
        public string OsArchitecture { get; set; }
        public string ProcessArchitecture { get; set; }
        public string BaseDirectory { get; set; }
        public string CurrentDirectory { get; set; }
        public string ScriptRoot { get; set; }
        public bool ChromePathExists { get; set; }
        public string ChromeBinary { get; set; }
        public List<string> ChromeArguments { get; set; }
    }

    public class NavigatorFingerprintRecord
    {
        public string UserAgent { get; set; }
        public bool? WebDriver { get; set; }
        public string Platform { get; set; }
        public List<string> Languages { get; set; }
        public string Language { get; set; }
        public string TimeZone { get; set; }
        public int? ScreenWidth { get; set; }
        public int? ScreenHeight { get; set; }
        public double? DevicePixelRatio { get; set; }
    }

    public class SelectorCountsRecord
    {
        public int HeaderCount { get; set; }
        public int ProductItemCount { get; set; }
        public int ProductTitleCount { get; set; }
        public int ProductPriceCount { get; set; }
        public int ProductColorCount { get; set; }
        public int CaptchaIframeCount { get; set; }
        public int ChallengeIframeCount { get; set; }
        public bool ConsentButtonPresent { get; set; }
    }

    public class StorageSummaryRecord
    {
        public int CookieCount { get; set; }
        public int LocalStorageCount { get; set; }
        public int SessionStorageCount { get; set; }
    }

    public class MainDocumentMetadataRecord
    {
        public int? StatusCode { get; set; }
        public string ContentType { get; set; }
        public string Server { get; set; }
        public string Location { get; set; }
        public string CacheControl { get; set; }
        public string CfRay { get; set; }
        public string XCache { get; set; }
        public string FetchError { get; set; }
    }

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
        public RuntimeInfoRecord RuntimeInfo { get; set; }
        public string BrowserVersion { get; set; }
        public string ChromeDriverVersion { get; set; }
        public string ConsentSelector { get; set; }
        public string PageSignal { get; set; }
        public string BlockReason { get; set; }
        public List<string> BlockSignals { get; set; }
        public NavigatorFingerprintRecord NavigatorFingerprint { get; set; }
        public SelectorCountsRecord SelectorCounts { get; set; }
        public StorageSummaryRecord StorageSummary { get; set; }
        public MainDocumentMetadataRecord MainDocumentMetadata { get; set; }
        public string ScreenshotPath { get; set; }
        public long? ReadyStateElapsedMs { get; set; }
        public long? PageSignalElapsedMs { get; set; }
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

        public static string ResolveArtifactDirectory(DateTime startedAtUtc, string artifactFolder)
        {
            var logFilePath = ResolveLogFilePath(startedAtUtc);
            var artifactDirectory = Path.Combine(Path.GetDirectoryName(logFilePath), artifactFolder);
            Directory.CreateDirectory(artifactDirectory);
            return artifactDirectory;
        }

        private static async Task<string> WriteSnapshotAsync(ScrapeRunRecord record, string htmlSnapshot, string logFilePath)
        {
            var snapshotDirectory = ResolveArtifactDirectory(record.StartedAtUtc, "snapshots");

            var snapshotPath = Path.Combine(snapshotDirectory, $"{record.StartedAtUtc:yyyyMMdd-HHmmss}-{record.RunId}.html");
            await File.WriteAllTextAsync(snapshotPath, htmlSnapshot);
            return snapshotPath;
        }
    }
}