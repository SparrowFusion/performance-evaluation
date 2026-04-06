using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace aerial_monitoring_system.Infrastructure.Metrics;

/// <summary>
/// Background service that detects and records .NET GC pauses.
/// Runs a tight polling loop and compares actual elapsed time against the expected poll interval.
/// Any delay significantly beyond the expected interval is attributed to a GC stop-the-world pause.
/// Writes to metrics/gc-*.csv.
/// </summary>
public sealed class GcMetricsService : BackgroundService
{
    private readonly ILogger<GcMetricsService> _logger;
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    // Poll every 10 ms. Any actual delay beyond this threshold is flagged as a suspected GC pause.
    private const int PollIntervalMs = 10;
    private const double PauseThresholdMs = 15.0;

    public GcMetricsService(ILogger<GcMetricsService> logger)
    {
        _logger = logger;
        Directory.CreateDirectory("metrics");
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _writer = new StreamWriter($"metrics/gc-{timestamp}.csv", append: false) { AutoFlush = true };
        _writer.WriteLine("Timestamp,Gen0Collections,Gen1Collections,Gen2Collections,SuspectedPauseMs");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int prevGen0 = GC.CollectionCount(0);
        int prevGen1 = GC.CollectionCount(1);
        int prevGen2 = GC.CollectionCount(2);

        var sw = Stopwatch.StartNew();

        while (!stoppingToken.IsCancellationRequested)
        {
            var before = sw.Elapsed;

            try { await Task.Delay(PollIntervalMs, stoppingToken); }
            catch (OperationCanceledException) { break; }

            double actualMs = (sw.Elapsed - before).TotalMilliseconds;

            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);

            bool gcOccurred = gen0 != prevGen0 || gen1 != prevGen1 || gen2 != prevGen2;
            double suspectedPauseMs = actualMs > PauseThresholdMs ? actualMs - PollIntervalMs : 0;

            if (gcOccurred || suspectedPauseMs > 0)
            {
                var line = $"{DateTime.UtcNow:O},{gen0},{gen1},{gen2},{suspectedPauseMs:F2}";
                lock (_lock) _writer.WriteLine(line);

                if (suspectedPauseMs > 0)
                    _logger.LogDebug(
                        "Suspected GC pause: {PauseMs:F1}ms (Gen0={G0}, Gen1={G1}, Gen2={G2})",
                        suspectedPauseMs, gen0, gen1, gen2);
            }

            prevGen0 = gen0;
            prevGen1 = gen1;
            prevGen2 = gen2;
        }
    }

    public override void Dispose()
    {
        _writer.Dispose();
        base.Dispose();
    }
}
