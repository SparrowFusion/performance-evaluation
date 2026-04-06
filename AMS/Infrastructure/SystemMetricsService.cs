using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace aerial_monitoring_system.Infrastructure.Metrics;

/// <summary>
/// Background service that periodically samples CPU and memory utilization of the AMS process.
/// Writes one row per second to metrics/system-*.csv.
/// Columns: Timestamp, CpuPercent, WorkingSetMb, GcAllocatedMb, ThreadCount
/// </summary>
public sealed class SystemMetricsService : BackgroundService
{
    private readonly ILogger<SystemMetricsService> _logger;
    private readonly StreamWriter _writer;
    private readonly Process _process = Process.GetCurrentProcess();
    private const int SampleIntervalMs = 1000;

    private TimeSpan _prevCpuTime;
    private DateTime _prevSampleTime;

    public SystemMetricsService(ILogger<SystemMetricsService> logger)
    {
        _logger = logger;
        Directory.CreateDirectory("metrics");
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _writer = new StreamWriter($"metrics/system-{timestamp}.csv", append: false) { AutoFlush = true };
        _writer.WriteLine("Timestamp,CpuPercent,WorkingSetMb,GcAllocatedMb,ThreadCount");

        _process.Refresh();
        _prevCpuTime = _process.TotalProcessorTime;
        _prevSampleTime = DateTime.UtcNow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(SampleIntervalMs, stoppingToken); }
            catch (OperationCanceledException) { break; }

            _process.Refresh();

            var now = DateTime.UtcNow;
            var cpuElapsed = _process.TotalProcessorTime - _prevCpuTime;
            double wallSeconds = (now - _prevSampleTime).TotalSeconds;

            double cpuPercent = wallSeconds > 0
                ? cpuElapsed.TotalSeconds / wallSeconds / Environment.ProcessorCount * 100.0
                : 0.0;

            double workingSetMb  = _process.WorkingSet64 / (1024.0 * 1024.0);
            double gcAllocatedMb = GC.GetTotalAllocatedBytes(precise: false) / (1024.0 * 1024.0);
            int    threadCount   = _process.Threads.Count;

            _writer.WriteLine($"{now:O},{cpuPercent:F1},{workingSetMb:F1},{gcAllocatedMb:F1},{threadCount}");

            _prevCpuTime    = _process.TotalProcessorTime;
            _prevSampleTime = now;
        }
    }

    public override void Dispose()
    {
        _writer.Dispose();
        _process.Dispose();
        base.Dispose();
    }
}
