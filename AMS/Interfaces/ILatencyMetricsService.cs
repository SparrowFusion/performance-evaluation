namespace aerial_monitoring_system.Interfaces;

public interface ILatencyMetricsService
{
    void RecordDetection(Guid radarId, double associationMs, double kalmanMs, int activeTrackCount);
    void RecordFusion(double fusionMs, int fusedTrackCount);
    void RecordSignalRDispatch(double dispatchMs);
}
