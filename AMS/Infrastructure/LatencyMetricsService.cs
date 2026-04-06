using aerial_monitoring_system.Interfaces;

namespace aerial_monitoring_system.Infrastructure.Metrics;

public sealed class LatencyMetricsService : ILatencyMetricsService, IDisposable
{
    private readonly StreamWriter _detectionWriter;
    private readonly StreamWriter _fusionWriter;
    private readonly StreamWriter _signalRWriter;
    private readonly object _detectionLock = new();
    private readonly object _fusionLock = new();
    private readonly object _signalRLock = new();

    public LatencyMetricsService()
    {
        Directory.CreateDirectory("metrics");
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _detectionWriter = new StreamWriter($"metrics/detections-{timestamp}.csv", append: false) { AutoFlush = true };
        _fusionWriter    = new StreamWriter($"metrics/fusion-{timestamp}.csv",    append: false) { AutoFlush = true };
        _signalRWriter   = new StreamWriter($"metrics/signalr-{timestamp}.csv",   append: false) { AutoFlush = true };
        _detectionWriter.WriteLine("Timestamp,RadarId,AssociationMs,KalmanMs,TotalMs,ActiveTrackCount");
        _fusionWriter.WriteLine("Timestamp,FusionMs,FusedTrackCount");
        _signalRWriter.WriteLine("Timestamp,DispatchMs");
    }

    public void RecordDetection(Guid radarId, double associationMs, double kalmanMs, int activeTrackCount)
    {
        var line = $"{DateTime.UtcNow:O},{radarId},{associationMs:F3},{kalmanMs:F3},{associationMs + kalmanMs:F3},{activeTrackCount}";
        lock (_detectionLock)
            _detectionWriter.WriteLine(line);
    }

    public void RecordFusion(double fusionMs, int fusedTrackCount)
    {
        var line = $"{DateTime.UtcNow:O},{fusionMs:F3},{fusedTrackCount}";
        lock (_fusionLock)
            _fusionWriter.WriteLine(line);
    }

    public void RecordSignalRDispatch(double dispatchMs)
    {
        var line = $"{DateTime.UtcNow:O},{dispatchMs:F3}";
        lock (_signalRLock)
            _signalRWriter.WriteLine(line);
    }

    public void Dispose()
    {
        _detectionWriter.Dispose();
        _fusionWriter.Dispose();
        _signalRWriter.Dispose();
    }
}
