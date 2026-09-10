using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NovaEmail.Storage;

namespace NovaEmail.PerformanceProbe;

internal static class Program
{
    private const int DefaultMessageCount = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var options = ParseArguments(args);
        var outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var runRoot = Path.Combine(
            Path.GetTempPath(), "NovaEmail.PerformanceProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        try
        {
            var dataRoot = Path.Combine(runRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            var initializeWatch = Stopwatch.StartNew();
            await store.InitializeAsync().ConfigureAwait(false);
            initializeWatch.Stop();

            var importWatch = Stopwatch.StartNew();
            for (var index = 0; index < options.MessageCount; index++)
            {
                var subject = $"Synthetic performance message {index:D6}";
                var body = $"Performance search needle {index:D6}. Offline history remains immutable.";
                var rawMime = Encoding.UTF8.GetBytes(
                    $"From: sender{index % 20}@novaemail.test\r\n" +
                    "To: recipient@novaemail.test\r\n" +
                    $"Message-ID: <performance-{index}@novaemail.test>\r\n" +
                    $"Subject: {subject}\r\n\r\n{body}\r\n");
                await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                    "performance-account", "inbox", index.ToString(CultureInfo.InvariantCulture),
                    "1", null, $"<performance-{index}@novaemail.test>", subject,
                    $"sender{index % 20}@novaemail.test", "recipient@novaemail.test",
                    DateTimeOffset.UnixEpoch.AddMinutes(index), rawMime, body, null, []))
                    .ConfigureAwait(false);
            }
            importWatch.Stop();

            _ = await store.SearchMessagesAsync("search needle", 100).ConfigureAwait(false);
            var searchSamples = new List<double>(50);
            var totalSearchResults = 0;
            for (var iteration = 0; iteration < 50; iteration++)
            {
                var searchWatch = Stopwatch.StartNew();
                var results = await store.SearchMessagesAsync("search needle", 100).ConfigureAwait(false);
                searchWatch.Stop();
                searchSamples.Add(searchWatch.Elapsed.TotalMilliseconds);
                totalSearchResults += results.Count;
            }
            searchSamples.Sort();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();

            var storedMessages = await store.GetMessageCountAsync().ConfigureAwait(false);
            var report = new PerformanceProbeReport(
                DateTimeOffset.UtcNow,
                options.MessageCount,
                storedMessages,
                initializeWatch.Elapsed.TotalMilliseconds,
                importWatch.Elapsed.TotalMilliseconds,
                importWatch.Elapsed.TotalMilliseconds / options.MessageCount,
                Percentile(searchSamples, 0.50),
                Percentile(searchSamples, 0.95),
                searchSamples[^1],
                totalSearchResults,
                process.WorkingSet64,
                SyntheticSmokePassed:
                    storedMessages == options.MessageCount &&
                    totalSearchResults == 5_000 &&
                    importWatch.Elapsed.TotalMilliseconds / options.MessageCount <= 250 &&
                    Percentile(searchSamples, 0.95) <= 250 &&
                    process.WorkingSet64 <= 768L * 1024L * 1024L,
                BaselineComparisonStatus: "not-run-no-external-baseline");
            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(report, JsonOptions),
                new UTF8Encoding(false)).ConfigureAwait(false);
            if (!report.SyntheticSmokePassed)
            {
                Console.Error.WriteLine("Synthetic performance smoke thresholds failed. See: " + outputPath);
                return 2;
            }
            Console.WriteLine("Synthetic performance smoke passed: " + outputPath);
            return 0;
        }
        finally
        {
            if (Directory.Exists(runRoot)) Directory.Delete(runRoot, recursive: true);
        }
    }

    private static double Percentile(List<double> orderedValues, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * orderedValues.Count) - 1;
        return orderedValues[Math.Clamp(index, 0, orderedValues.Count - 1)];
    }

    private static ProbeOptions ParseArguments(string[] args)
    {
        string? output = null;
        var messages = DefaultMessageCount;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--messages" when index + 1 < args.Length &&
                                               int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed):
                    messages = parsed;
                    break;
                default:
                    throw new ArgumentException("Usage: --output <json-path> [--messages <100..10000>]");
            }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        if (messages is < 100 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(args), "Message count must be between 100 and 10,000.");
        return new ProbeOptions(output, messages);
    }

    private sealed record ProbeOptions(string OutputPath, int MessageCount);
    private sealed record PerformanceProbeReport(
        DateTimeOffset GeneratedUtc,
        int RequestedMessages,
        long StoredMessages,
        double InitializeMilliseconds,
        double ImportMilliseconds,
        double ImportMillisecondsPerMessage,
        double SearchP50Milliseconds,
        double SearchP95Milliseconds,
        double SearchMaximumMilliseconds,
        int TotalSearchResults,
        long WorkingSetBytes,
        bool SyntheticSmokePassed,
        string BaselineComparisonStatus);
}
