using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using aerial_monitoring_system.Events;
using aerial_monitoring_system.Infrastructure;
using aerial_monitoring_system.Interfaces;
using aerial_monitoring_system.Models;

namespace aerial_monitoring_system.Actors;

/// <summary>
/// Central manager responsible for coordinating track creation, association, and lifecycle management.
/// Uses per-radar track pools to prevent cross-contamination of Kalman filter estimates 
/// (ensures each radar's bias and noise characteristics are handled independently).
/// </summary>
public class CentralTrackManager
{
    private readonly Dictionary<Guid, RadarTrackPool> _radarTrackPools;
    private readonly IGatingAlgorithm _gating;
    private readonly IGlobalNearestNeighbor _globalNearestNeighbor;
    private readonly IEventBus _eventBus;
    private readonly ITrackActorFactory _trackActorFactory;
    private readonly IOverlapZoneDetector _overlapZoneDetector;
    private readonly RadarSystemConfiguration _radarConfig;
    private readonly ILogger<CentralTrackManager> _logger;
    private readonly ILatencyMetricsService _metrics;

    // Configuration constants
    private const double SearchRadiusMeters = 1000.0; // 1km search radius for candidate tracks

    /// <summary>
    /// Container for radar-specific tracking data. 
    /// Maintains an independent spatial index and set of actors to isolate filtering logic per radar.
    /// </summary>    
    private class RadarTrackPool
    {
        public ISpatialIndex SpatialIndex { get; }
        public Dictionary<Guid, TrackActor> TrackActors { get; }
        /// <summary>
        /// Prevents a single radar hit from being associated with multiple tracks 
        /// or a single track from accepting multiple hits within the same scan/dwell.
        /// </summary>
        public HashSet<Guid> AssignedThisBatch { get; }

        public RadarTrackPool()
        {
            SpatialIndex = new RBushSpatialIndex();
            TrackActors = new Dictionary<Guid, TrackActor>();
            AssignedThisBatch = new HashSet<Guid>();
        }
    }

    public CentralTrackManager(
        IGatingAlgorithm gating,
        IGlobalNearestNeighbor globalNearestNeighbor,
        IEventBus eventBus,
        ITrackActorFactory trackActorFactory,
        IOverlapZoneDetector overlapZoneDetector,
        IOptions<RadarSystemConfiguration> radarConfig,
        ILogger<CentralTrackManager> logger,
        ILatencyMetricsService metrics)
    {
        _gating = gating;
        _globalNearestNeighbor = globalNearestNeighbor;
        _eventBus = eventBus;
        _trackActorFactory = trackActorFactory;
        _overlapZoneDetector = overlapZoneDetector;
        _radarConfig = radarConfig.Value;
        _radarTrackPools = new Dictionary<Guid, RadarTrackPool>();
        _logger = logger;
        _metrics = metrics;

        // Initialize overlap zone detector with radar configuration
        _overlapZoneDetector.Initialize(_radarConfig);

        // Subscribe to radar hit events
        _eventBus.Subscribe<RadarHitReceived>(HandleRadarHit);
        _eventBus.Subscribe<RadarBatchCompleted>(HandleRadarBatchCompleted);
    }

    /// <summary>
    /// Gets or creates a radar-specific track pool.
    /// </summary>
    private RadarTrackPool GetOrCreateRadarPool(Guid radarId)
    {
        if (!_radarTrackPools.TryGetValue(radarId, out var pool))
        {
            pool = new RadarTrackPool();
            _radarTrackPools[radarId] = pool;
        }
        return pool;
    }

    /// <summary>
    /// Processes a radar hit. Follows the standard tracking pipeline: 
    /// 1. Coordinate Conversion -> 2. Spatial Discovery -> 3. Gating/Data Association -> 4. State Update.
    /// </summary>
    public void HandleRadarHit(RadarHitReceived hit)
    {

        // Get this radar's track pool
        var pool = GetOrCreateRadarPool(hit.RadarId);

        // Step 1: Convert Geodetic (Lat/Lon) to Local Cartesian (XYZ) for Euclidean distance calculations.
        Position position = CoordinateConverter.ToLocalCartesian(hit.LatitudeDeg, hit.LongitudeDeg);

        // Step 2: Preserve original polar data (Range/Azimuth) for downstream multi-lateration/fusion.
        RawMeasurement? rawMeasurement = CreateRawMeasurement(hit, position);

        var associationSw = Stopwatch.StartNew();

        // Step 3: Find candidate tracks within search radius (in THIS RADAR'S pool only)
        List<Guid> candidateTrackIds = FindCandidateTracks(pool, position);

        // Step 4: Decision logic based on number of candidates
        Guid assignedTrackId;
        if (candidateTrackIds.Count == 0)
        {
            associationSw.Stop();
            var kalmanSw = Stopwatch.StartNew();
            // No nearby tracks - create new track
            assignedTrackId = CreateNewTrack(pool, hit, position);
            kalmanSw.Stop();
            _metrics.RecordDetection(hit.RadarId, associationSw.Elapsed.TotalMilliseconds, kalmanSw.Elapsed.TotalMilliseconds, pool.TrackActors.Count);
            _logger.LogDebug("Created new track from unassociated hit");
        }
        else
        {

            // Get candidate track actors
            var candidateTrackActors = candidateTrackIds
                .Where(id => pool.TrackActors.ContainsKey(id))
                .Select(id => pool.TrackActors[id])
                .ToList();

            // Create measurement from radar hit
            var measurement = new Measurement(
                RadarId: hit.RadarId,
                Timestamp: hit.Timestamp,
                Range: hit.RangeNmi,
                Azimuth: hit.Azimuth,
                SignalStrength: hit.Intensity,
                Position: position
            );

            // Apply gating (statistical distance check) to prune candidates that are spatially 
            // near but kinematically improbable.
            var gatedTrackActors = ApplyGating(measurement, candidateTrackActors);
            associationSw.Stop();

            var kalmanSw = Stopwatch.StartNew();
            if (gatedTrackActors.Count == 0)
            {
                // No tracks passed statistical gating - treat as a new target discovery.
                assignedTrackId = CreateNewTrack(pool, hit, position);
            }
            else if (gatedTrackActors.Count == 1)
            {
                // Unambiguous association: only one track is statistically consistent with the hit.
                assignedTrackId = gatedTrackActors[0].GetTrack().TrackId;
                AssociateHitToTrack(pool, assignedTrackId, hit, position);
            }
            else
            {
                // Ambiguous association: multiple tracks are consistent. Use GNN (Mahalanobis distance) 
                // to find the most likely match - select nearest track.
                Guid? selectedTrackId = SelectNearestTrack(measurement, gatedTrackActors);

                if (selectedTrackId.HasValue)
                {
                    assignedTrackId = selectedTrackId.Value;
                    AssociateHitToTrack(pool, assignedTrackId, hit, position);
                }
                else
                {
                    // No track selected - create new track
                    assignedTrackId = CreateNewTrack(pool, hit, position);
                }
            }
            kalmanSw.Stop();
            _metrics.RecordDetection(hit.RadarId, associationSw.Elapsed.TotalMilliseconds, kalmanSw.Elapsed.TotalMilliseconds, pool.TrackActors.Count);
        }

        // Step 5: Check if hit is in overlap zone and publish event for fusion layer
        if (rawMeasurement != null)
        {
            bool inOverlapZone = _overlapZoneDetector.IsInOverlapZone(position, hit.RadarId);

            if (inOverlapZone)
            {
                // Get track velocity for fusion correlation (helps predict position at common time)
                Velocity? trackVelocity = null;
                if (pool.TrackActors.TryGetValue(assignedTrackId, out var trackActor))
                {
                    trackVelocity = trackActor.GetTrack().Velocity;
                }

                _eventBus.Publish(new HitInOverlapZone(
                    RadarId: hit.RadarId,
                    PerRadarTrackId: assignedTrackId,
                    RawMeasurement: rawMeasurement,
                    Timestamp: hit.Timestamp,
                    TrackVelocity: trackVelocity
                ));
            }
        }
    }

    /// <summary>
    /// Creates a RawMeasurement from a radar hit, preserving polar coordinates for triangulation.
    /// </summary>
    private RawMeasurement? CreateRawMeasurement(RadarHitReceived hit, Position projectedPosition)
    {
        var radarConfig = _radarConfig.Radars.FirstOrDefault(r => r.RadarId == hit.RadarId);
        if (radarConfig == null)
        {
            return null;
        }

        Position radarPosition = CoordinateConverter.ToLocalCartesian(
            radarConfig.LatitudeDeg,
            radarConfig.LongitudeDeg
        );

        double slantRangeMeters = hit.RangeNmi * 1852.0; // NMI to meters

        return new RawMeasurement(
            RadarId: hit.RadarId,
            Timestamp: hit.Timestamp,
            SlantRangeMeters: slantRangeMeters,
            AzimuthDeg: hit.Azimuth,
            RadarPosition: radarPosition,
            RadarAltitudeMeters: radarConfig.AltitudeMeters,
            ProjectedPosition: projectedPosition,
            Intensity: hit.Intensity
        );
    }

    /// <summary>
    /// Finds candidate tracks within search radius of a position in the given radar's pool.
    /// Excludes tracks already assigned during this radar batch.
    /// </summary>
    private List<Guid> FindCandidateTracks(RadarTrackPool pool, Position position)
    {
        var allCandidates = pool.SpatialIndex.RangeQuery(position, SearchRadiusMeters);

        // Filter out tracks already assigned a hit from this radar's current scan
        return allCandidates
            .Where(id => !pool.AssignedThisBatch.Contains(id))
            .ToList();
    }

    /// <summary>
    /// Associates a radar hit to an existing track.
    /// Marks the track as assigned for this radar's current scan to prevent double-assignment.
    /// </summary>
    private void AssociateHitToTrack(RadarTrackPool pool, Guid trackId, RadarHitReceived hit, Position position)
    {
        if (!pool.TrackActors.TryGetValue(trackId, out var trackActor))
        {
            return;
        }

        // Mark track as assigned for this radar's scan (prevents double-assignment)
        pool.AssignedThisBatch.Add(trackId);

        // Update TrackActor with new hit (this updates the Kalman filter)
        var oldState = trackActor.State;
        var hitAssociated = new HitAssociated(trackId, hit);
        trackActor.HandleHitAssociated(hitAssociated);
        var newState = trackActor.State;

        // Publish state change event if state changed (e.g., Potential → Verified)
        if (oldState != newState)
        {
            _eventBus.Publish(new ParentTrackStateChanged(
                ParentTrackId: trackId,
                RadarId: hit.RadarId,
                OldState: oldState,
                NewState: newState,
                Timestamp: hit.Timestamp
            ));
        }

        // Update spatial index with smoothed position from Kalman filter
        pool.SpatialIndex.Update(trackId, trackActor.GetSmoothedPosition());
    }

    /// <summary>
    /// Creates a new track from an unassociated radar hit.
    /// </summary>
    private Guid CreateNewTrack(RadarTrackPool pool, RadarHitReceived hit, Position position)
    {
        Guid trackId = Guid.NewGuid();
        TrackActor trackActor = _trackActorFactory.Create(trackId);
        pool.TrackActors[trackId] = trackActor;

        // Initialize the track with the first hit (this initializes the Kalman filter)
        trackActor.HandleInitialize(new InitializeTrack(trackId, hit));

        // Mark track as assigned so batch completion doesn't count it as a miss
        pool.AssignedThisBatch.Add(trackId);

        // Insert track into spatial index using smoothed position from Kalman filter
        var smoothedPosition = trackActor.GetSmoothedPosition();
        pool.SpatialIndex.Insert(trackId, smoothedPosition);

        return trackId;
    }

    /// <summary>
    /// Finalizes the radar scan. Updates the state of tracks that were NOT updated during 
    /// this batch (coasting) and prunes tracks that have exceeded missed-hit thresholds.
    /// </summary>
    public void HandleRadarBatchCompleted(RadarBatchCompleted batch)
    {
        if (!_radarTrackPools.TryGetValue(batch.RadarId, out var pool))
        {
            // No tracks for this radar yet
            return;
        }

        var deadTrackIds = new List<Guid>();

        // Notify all tracks that weren't hit this batch
        foreach (var (trackId, trackActor) in pool.TrackActors)
        {
            if (!pool.AssignedThisBatch.Contains(trackId))
            {
                var oldState = trackActor.State;
                // Notify track of a missed detection to increment drop-track counters.
                trackActor.HandleMissedScan(batch.RadarId);
                var newState = trackActor.State;

                // Publish state change event if state changed
                if (oldState != newState)
                {
                    _eventBus.Publish(new ParentTrackStateChanged(
                        ParentTrackId: trackId,
                        RadarId: batch.RadarId,
                        OldState: oldState,
                        NewState: newState,
                        Timestamp: batch.Timestamp
                    ));
                }

                // Check track state after handling missed scan
                if (trackActor.IsDead)
                {
                    deadTrackIds.Add(trackId);
                }
                else if (trackActor.IsCoasting)
                {
                    // For tracks without hits, update the spatial index using the
                    // Kalman-predicted position to ensure they remain discoverable in the next scan.
                    var predictedPosition = trackActor.GetPredictedPosition(batch.Timestamp);
                    pool.SpatialIndex.Update(trackId, predictedPosition);

                    // Publish prediction to frontend so coasting tracks show extrapolated position
                    _eventBus.Publish(new TrackPredictionUpdated(
                        TrackId: trackId,
                        State: TrackState.Coasting,
                        PredictedPosition: predictedPosition,
                        PredictionTime: batch.Timestamp,
                        PassedGating: false
                    ));
                }
            }
        }

        // Remove dead tracks from the system (can't remove during iteration)
        foreach (var trackId in deadTrackIds)
        {
            var trackActor = pool.TrackActors[trackId];

            // Publish death event (for FusionTrackManager to clean up)
            _eventBus.Publish(new ParentTrackDied(
                ParentTrackId: trackId,
                RadarId: batch.RadarId,
                FinalState: trackActor.State,
                Timestamp: batch.Timestamp
            ));

            pool.TrackActors.Remove(trackId);
            pool.SpatialIndex.Delete(trackId);
        }

        pool.AssignedThisBatch.Clear();
    }

    /// <summary>
    /// Helper function to apply gating algorithm to filter candidate tracks.
    /// </summary>
    private List<TrackActor> ApplyGating(Measurement measurement, List<TrackActor> candidateTrackActors)
    {
        return _gating.Gate(measurement, candidateTrackActors);
    }

    /// <summary>
    /// Selects the nearest track from gated candidates using Mahalanobis distance.
    /// </summary>
    private Guid? SelectNearestTrack(Measurement measurement, List<TrackActor> gatedTrackActors)
    {
        return _globalNearestNeighbor.AssignSingle(measurement, gatedTrackActors);
    }

    /// <summary>
    /// Gets the total number of active tracks across all radars.
    /// </summary>
    public int TotalTrackCount => _radarTrackPools.Values.Sum(p => p.TrackActors.Count);

    /// <summary>
    /// Gets the track count for a specific radar.
    /// </summary>
    public int GetTrackCount(Guid radarId)
    {
        return _radarTrackPools.TryGetValue(radarId, out var pool) ? pool.TrackActors.Count : 0;
    }
}
