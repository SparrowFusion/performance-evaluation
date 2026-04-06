using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using aerial_monitoring_system.Events;
using aerial_monitoring_system.Interfaces;
using aerial_monitoring_system.Models;
using ClientConnectionInfo = aerial_monitoring_system.Models.ConnectionInfo;

namespace aerial_monitoring_system.Actors;

/// <summary>
/// Actor responsible for broadcasting track updates to connected SignalR clients.
/// </summary>
public class SignalRHubActor
{
    private readonly IHubContext<TrackHub> _hubContext;
    private readonly Dictionary<string, ClientConnectionInfo> _clients;
    private readonly IEventBus _eventBus;
    private readonly ILatencyMetricsService _metrics;
    private readonly Dictionary<string, DateTime> _lastUpdateTime;
    private readonly TimeSpan _throttleInterval = TimeSpan.FromMilliseconds(100);

    public SignalRHubActor(
        IHubContext<TrackHub> hubContext,
        IEventBus eventBus,
        ILatencyMetricsService metrics)
    {
        _hubContext = hubContext;
        _eventBus = eventBus;
        _metrics = metrics;
        _clients = new Dictionary<string, ClientConnectionInfo>();
        _lastUpdateTime = new Dictionary<string, DateTime>();

        // Subscribe to track events
        _eventBus.Subscribe<TrackUpdated>(OnTrackUpdated);
        _eventBus.Subscribe<TrackConfirmedEvent>(OnTrackConfirmed);
        _eventBus.Subscribe<AltitudeUpdated>(OnAltitudeUpdated);
        _eventBus.Subscribe<TrackPredictionUpdated>(OnTrackPredictionUpdated);

        // Subscribe to fused track events (3D tracks with altitude from triangulation)
        _eventBus.Subscribe<FusedTrackUpdated>(OnFusedTrackUpdated);
        _eventBus.Subscribe<FusedTrackConfirmed>(OnFusedTrackConfirmed);
    }

    private void OnTrackUpdated(TrackUpdated update)
    {
        _ = HandleTrackUpdate(update);
    }

    private void OnTrackConfirmed(TrackConfirmedEvent confirmed)
    {
        _ = HandleTrackConfirmed(confirmed);
    }

    private void OnAltitudeUpdated(AltitudeUpdated update)
    {
        _ = HandleAltitudeUpdate(update);
    }

    private void OnTrackPredictionUpdated(TrackPredictionUpdated update)
    {
        _ = HandleTrackPredictionUpdate(update);
    }

    private void OnFusedTrackUpdated(FusedTrackUpdated update)
    {
        _ = HandleFusedTrackUpdate(update);
    }

    private void OnFusedTrackConfirmed(FusedTrackConfirmed confirmed)
    {
        _ = HandleFusedTrackConfirmed(confirmed);
    }

    /// <summary>
    /// Handles a track update event by broadcasting to clients.
    /// </summary>
    public async Task HandleTrackUpdate(TrackUpdated update)
    {
        var sw = Stopwatch.StartNew();
        await _hubContext.Clients.All.SendAsync("TrackUpdated", new
        {
            update.TrackId,
            State = update.State.ToString(),
            Position = new { update.Position.X, update.Position.Y },
            Velocity = new { update.Velocity.Vx, update.Velocity.Vy },
            update.Timestamp
        });
        sw.Stop();
        _metrics.RecordSignalRDispatch(sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Handles a track confirmed event by broadcasting to clients.
    /// </summary>
    public async Task HandleTrackConfirmed(TrackConfirmedEvent confirmed)
    {
        await _hubContext.Clients.All.SendAsync("TrackConfirmed", new
        {
            confirmed.TrackId,
            confirmed.RadarId,
            Position = new { confirmed.Position.X, confirmed.Position.Y },
            Velocity = new { confirmed.Velocity.Vx, confirmed.Velocity.Vy },
            confirmed.Timestamp
        });
    }

    /// <summary>
    /// Handles an altitude update event by broadcasting to clients.
    /// </summary>
    public async Task HandleAltitudeUpdate(AltitudeUpdated update)
    {
        await _hubContext.Clients.All.SendAsync("AltitudeUpdated", new
        {
            update.AltitudeTrackId,
            FusedPosition = new
            {
                update.FusedPosition.X,
                update.FusedPosition.Y,
                update.FusedPosition.Z
            },
            update.Altitude,
            update.AltitudeUncertainty,
            update.Timestamp
        });
    }

    /// <summary>
    /// Handles a track prediction event by broadcasting to clients.
    /// </summary>
    public async Task HandleTrackPredictionUpdate(TrackPredictionUpdated update)
    {
        await _hubContext.Clients.All.SendAsync("TrackPredictionUpdated", new
        {
            update.TrackId,
            State = update.State.ToString(),
            PredictedPosition = new { update.PredictedPosition.X, update.PredictedPosition.Y },
            PredictionTime = update.PredictionTime,
            update.PassedGating
        });
    }

    /// <summary>
    /// Handles a fused track update event by broadcasting 3D position to clients.
    /// </summary>
    public async Task HandleFusedTrackUpdate(FusedTrackUpdated update)
    {
        await _hubContext.Clients.All.SendAsync("FusedTrackUpdated", new
        {
            update.FusedTrackId,
            State = update.State.ToString(),
            Position3D = new
            {
                update.Position3D.X,
                update.Position3D.Y,
                update.Position3D.Z
            },
            update.AltitudeMeters,
            update.AltitudeUncertainty,
            Velocity = new { update.Velocity.Vx, update.Velocity.Vy },
            update.Timestamp,
            update.ContributingTrackIds
        });
    }

    /// <summary>
    /// Handles a fused track confirmed event by broadcasting to clients.
    /// </summary>
    public async Task HandleFusedTrackConfirmed(FusedTrackConfirmed confirmed)
    {
        await _hubContext.Clients.All.SendAsync("FusedTrackConfirmed", new
        {
            confirmed.FusedTrackId,
            Position3D = new
            {
                confirmed.Position3D.X,
                confirmed.Position3D.Y,
                confirmed.Position3D.Z
            },
            confirmed.AltitudeMeters,
            confirmed.Timestamp
        });
    }

    /// <summary>
    /// Handles a new client connection.
    /// </summary>
    public void HandleClientConnected(string connectionId)
    {
        _clients[connectionId] = new ClientConnectionInfo(
            ConnectionId: connectionId,
            ConnectedAt: DateTime.UtcNow,
            LastMessageTime: null
        );
    }

    /// <summary>
    /// Handles a client disconnection.
    /// </summary>
    public void HandleClientDisconnected(string connectionId)
    {
        _clients.Remove(connectionId);
        _lastUpdateTime.Remove(connectionId);
    }

    /// <summary>
    /// Throttles update messages to prevent overwhelming clients.
    /// </summary>
    private bool ShouldThrottle(string connectionId)
    {
        if (!_lastUpdateTime.TryGetValue(connectionId, out var lastTime))
        {
            _lastUpdateTime[connectionId] = DateTime.UtcNow;
            return false;
        }

        if (DateTime.UtcNow - lastTime < _throttleInterval)
        {
            return true;
        }

        _lastUpdateTime[connectionId] = DateTime.UtcNow;
        return false;
    }
}

/// <summary>
/// SignalR Hub for track updates.
/// </summary>
public class TrackHub : Hub
{
    private readonly SignalRHubActor _hubActor;

    public TrackHub(SignalRHubActor hubActor)
    {
        _hubActor = hubActor;
    }

    public override Task OnConnectedAsync()
    {
        _hubActor.HandleClientConnected(Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _hubActor.HandleClientDisconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
