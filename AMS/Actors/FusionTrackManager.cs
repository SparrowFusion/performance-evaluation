using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using aerial_monitoring_system.Events;
using aerial_monitoring_system.Interfaces;
using aerial_monitoring_system.Models;

namespace aerial_monitoring_system.Actors;

/// <summary>
/// Manager responsible for fusing tracks from multiple radars to estimate altitude.
/// Operates exclusively on hits in the overlap zone where dual-radar triangulation is possible.
/// </summary>
public class FusionTrackManager
{
    // Spatial index for per-radar tracks currently residing in the overlap zone.
    // Stores track GUIDs rather than raw hits to allow for track-to-track correlation.
    private readonly ISpatialIndex _parentTrackIndex;

    private readonly ITriangulationSolver _triangulationSolver;
    private readonly Dictionary<Guid, FusedTrackActor> _fusedTracks;
    private readonly IEventBus _eventBus;
    private readonly ILogger<FusionTrackManager> _logger;
    private readonly ILatencyMetricsService _metrics;
    private readonly RadarSystemConfiguration _config;

    // Source of truth for per-radar track metadata used for correlation and triangulation.
    private readonly Dictionary<Guid, ParentTrackInfo> _parentTracks;

    // Bidirectional correlation maps:
    // _parentToFusedMap: Provides O(1) lookup when a radar hit arrives to find its target fusion.
    // _fusedToParentsMap: Provides O(1) lookup to find sibling tracks from other radars.
    private readonly Dictionary<Guid, Guid> _parentToFusedMap;  // parent → fused
    private readonly Dictionary<Guid, HashSet<Guid>> _fusedToParentsMap;  // fused → parents

    // Tracking set used during batch completion to identify fused tracks that missed updates.
    private readonly HashSet<Guid> _fusedTracksUpdatedThisBatch;

    /// <summary>
    /// Metadata for a per-radar track in the overlap zone.
    /// Stores measurement history to allow for sliding-window triangulation.
    /// </summary>
    private class ParentTrackInfo
    {
        public required Guid ParentTrackId { get; set; }
        public required Guid RadarId { get; set; }
        public required Position LastPosition { get; set; }
        public required DateTime LastUpdateTime { get; set; }
        public Guid? FusedTrackId { get; set; }                         // null if not fused
        public required RawMeasurement LastMeasurement { get; set; }
        public RawMeasurement? PreviousMeasurement { get; set; }        // Historical measurement used to mitigate staggered scan times.
        public required TrackState State { get; set; }                  // Synchronized state for coasting and death logic.
        public Velocity? Velocity { get; set; }                         // Estimated vector used for kinematic consistency checks.
    }

    public FusionTrackManager(
        ISpatialIndex parentTrackIndex,
        ITriangulationSolver triangulationSolver,
        IEventBus eventBus,
        IOptions<RadarSystemConfiguration> config,
        ILogger<FusionTrackManager> logger,
        ILatencyMetricsService metrics)
    {
        _parentTrackIndex = parentTrackIndex;
        _triangulationSolver = triangulationSolver;
        _eventBus = eventBus;
        _config = config.Value;
        _logger = logger;
        _metrics = metrics;
        _fusedTracks = new Dictionary<Guid, FusedTrackActor>();
        _parentTracks = new Dictionary<Guid, ParentTrackInfo>();
        _parentToFusedMap = new Dictionary<Guid, Guid>();
        _fusedToParentsMap = new Dictionary<Guid, HashSet<Guid>>();
        _fusedTracksUpdatedThisBatch = new HashSet<Guid>();

        // Subscribe to events
        _eventBus.Subscribe<HitInOverlapZone>(HandleHitInOverlapZone);
        _eventBus.Subscribe<RadarBatchCompleted>(HandleRadarBatchCompleted);
        _eventBus.Subscribe<ParentTrackDied>(HandleParentTrackDied);
        _eventBus.Subscribe<ParentTrackStateChanged>(HandleParentTrackStateChanged);

        _logger.LogInformation(
            "FusionTrackManager initialized. OverlapSearchRadius={Radius}m, MaxTimeDelta={TimeDelta}s",
            _config.OverlapSearchRadiusMeters, _config.MaxHitTimeDeltaSeconds);
    }

    /// <summary>
    /// Entry point for hits in the overlap zone. 
    /// Determines if the track is already fused or requires a new correlation search.
    /// </summary>
    public void HandleHitInOverlapZone(HitInOverlapZone hit)
    {
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "HitInOverlapZone received: Radar={RadarId}, Track={TrackId}, Position=({X:F0}, {Y:F0})",
            evt.RadarId, evt.TrackId,
            evt.RawMeasurement.ProjectedPosition.X, evt.RawMeasurement.ProjectedPosition.Y);

        // Check if the incoming track is already part of an active fusion.
        if (_parentToFusedMap.TryGetValue(hit.PerRadarTrackId, out var fusedId))
        {
            // Fast path: Direct update to existing fused track
            UpdateExistingFusedTrack(fusedId, evt);
            return;
        }

        // Search the spatial index for potential sibling tracks from OPPOSITE radars.
        var nearbyParentTrackIds = _parentTrackIndex.RangeQuery(
            hit.RawMeasurement.ProjectedPosition,
            _config.OverlapSearchRadiusMeters
        );

        _logger.LogInformation(
            "Parent track index query: Found {TotalCount} nearby parent tracks within {Radius}m",
            nearbyParentTrackIds.Count, _config.OverlapSearchRadiusMeters);

        // Filter out tracks from the same radar source.
        var candidates = nearbyParentTrackIds
            .Where(id => _parentTracks.ContainsKey(id))
            .Select(id => _parentTracks[id])
            .Where(p => p.RadarId != hit.RadarId)
            .ToList();

        _logger.LogInformation(
            "Candidates after radar filtering: {Count} from opposite radar",
            candidates.Count);

        // Capture the previous state of this track before it is updated with the new hit.
        RawMeasurement? parent1Previous = _parentTracks.TryGetValue(hit.PerRadarTrackId, out var p1Info)
            ? p1Info.LastMeasurement
            : null;

        var validSolutions = new List<(ParentTrackInfo candidate, TriangulationSolution solution)>();

        foreach (var candidate in candidates)
        {
            // Verify kinematic consistency (speed and heading).
            if (!IsVelocityConsistent(hit.TrackVelocity, candidate.Velocity))
            {
                _logger.LogDebug(
                    "Rejected candidate {CandidateId}: velocity inconsistent",
                    candidate.ParentTrackId);
                continue;
            }

            // Verify spatial proximity based on slant-range error margins.
            if (!IsSpatiallyConsistent(hit.RawMeasurement, candidate.LastMeasurement))
            {
                _logger.LogDebug(
                    "Rejected candidate {CandidateId}: spatial inconsistent",
                    candidate.ParentTrackId);
                continue;
            }

            _logger.LogInformation(
                "CORRELATION PASSED for tracks {TrackA} and {TrackB} - attempting triangulation",
                hit.PerRadarTrackId, candidate.ParentTrackId);

            // Try all temporally-valid measurement combinations and pick the best result.
            // This avoids velocity prediction entirely: rather than estimating where one track
            // would be at the other's scan time, we use whichever pairing of actual hits
            // is closest in time and produces the best geometry.
            var solution = TryBestTriangulation(
                hit.RawMeasurement, parent1Previous,
                candidate.LastMeasurement, candidate.PreviousMeasurement);

            if (solution != null)
            {
                validSolutions.Add((candidate, solution));
                _logger.LogInformation(
                    "TRIANGULATION SUCCESS: Alt={Alt:F0}m, GDOP={GDOP:F2}, Uncertainty={Unc:F0}m",
                    solution.AltitudeMeters, solution.GDOP, solution.AltitudeUncertainty);
            }
            else
            {
                _logger.LogWarning(
                    "TRIANGULATION FAILED after correlation passed for tracks {TrackA} and {TrackB}. " +
                    "This needs investigation - check TriangulationSolver logs for details.",
                    hit.PerRadarTrackId, candidate.ParentTrackId);
            }
        }

        // Update measurement history regardless of whether fusion succeeded.
        AddOrUpdateParentTrackMeasurements(evt);

        // Select best solution and create/update fused track
        if (validSolutions.Count > 0)
        {
            var best = validSolutions
                .OrderBy(s => s.solution.GDOP)
                .ThenBy(s => Math.Abs((s.solution.Timestamp1 - evt.Timestamp).TotalSeconds) +
                             Math.Abs((s.solution.Timestamp2 - evt.Timestamp).TotalSeconds))
                .First();

            CreateFusedTrack(evt.TrackId, best.candidateTrackId, best.solution);
        }

        // Always update measurement history for this track (whether fused or not).
        AddOrUpdateParentTrackMeasurements(evt);
    }

    /// <summary>
    /// Updates an existing fused track with a new hit from one of its parent tracks.
    /// </summary>
    private void UpdateExistingFusedTrack(Guid fusedId, HitInOverlapZone hit)
    {
        if (!_fusedTracks.TryGetValue(fusedId, out var fusedActor))
        {
            _logger.LogWarning("Fused track {FusedId} not found", fusedId);
            return;
        }

        // Get sibling parent track (the other radar's track in this fusion)
        var parentTracks = _fusedToParentsMap[fusedId];
        var siblingId = parentTracks.FirstOrDefault(p => p != hit.PerRadarTrackId);

        if (siblingId == Guid.Empty || !_parentTracks.TryGetValue(siblingId, out var sibling))
        {
            _logger.LogWarning("Sibling parent track not found for fusion update");
            return;
        }

        // Capture parent1's previous hit before metadata is updated.
        RawMeasurement? parent1Previous = _parentTracks.TryGetValue(hit.PerRadarTrackId, out var p1Info)
            ? p1Info.LastMeasurement
            : null;

        // Try all temporally-valid measurement pairings: current/previous from each side.
        // TryBestTriangulation skips any pair whose timestamp delta exceeds MaxHitTimeDeltaSeconds,
        // so there is no need for a separate age guard here.
        var solution = TryBestTriangulation(
            hit.RawMeasurement, parent1Previous,
            sibling.LastMeasurement, sibling.PreviousMeasurement);

        if (solution != null)
        {
            fusedActor.HandleNewSolution(solution);
            _fusedTracksUpdatedThisBatch.Add(fusedId);
            UpdateParentTrackMetadata(hit);

            _logger.LogDebug(
                "Updated fused track {FusedId} with new solution, altitude={Alt:F0}m",
                fusedId, solution.AltitudeMeters);
        }
        else
        {
            fusedActor.HandleTriangulationFailure(hit.RawMeasurement.Timestamp);

            _logger.LogWarning(
                "Triangulation failed for fused track {FusedId} update",
                fusedId);
        }
    }

    /// <summary>
    /// Creates a new fused track from two parent tracks after successful triangulation.
    /// </summary>
    private void CreateFusedTrack(Guid parentA, Guid parentB, TriangulationSolution solution, HitInOverlapZone hit)
    {
        var fusedId = Guid.NewGuid();
        var fusedActor = new FusedTrackActor(
            fusedId,
            new List<Guid> { parentA, parentB },
            _eventBus
        );
        fusedActor.HandleNewSolution(solution);

        // Store fused track
        _fusedTracks[fusedId] = fusedActor;
        _fusedTracksUpdatedThisBatch.Add(fusedId);

        // Update correlation maps
        _parentToFusedMap[parentA] = fusedId;
        _parentToFusedMap[parentB] = fusedId;
        _fusedToParentsMap[fusedId] = new HashSet<Guid> { parentA, parentB };

        // Mark parent tracks as fused
        _parentTracks[parentA].FusedTrackId = fusedId;
        _parentTracks[parentB].FusedTrackId = fusedId;

        // Removing parents from the spatial index ensures they are no longer discovery candidates.
        _parentTrackIndex.Delete(parentA);
        _parentTrackIndex.Delete(parentB);

        // Update metadata for current hit's parent track
        UpdateParentTrackMetadata(hit);

        _logger.LogInformation(
            "Created fused track {FusedId} from parents {ParentA} and {ParentB}, altitude={Alt:F0}m, GDOP={GDOP:F2}",
            fusedId, parentA, parentB, solution.AltitudeMeters, solution.GDOP);
    }

    /// <summary>
    /// Adds or updates a parent track in the spatial index when no fusion is found.
    /// </summary>
    private void AddOrUpdateParentTrack(HitInOverlapZone hit)
    {
        var parentId = hit.PerRadarTrackId;

        if (_parentTracks.TryGetValue(parentId, out var existing))
        {
            // Update existing
            existing.LastPosition = hit.RawMeasurement.ProjectedPosition;
            existing.LastUpdateTime = hit.RawMeasurement.Timestamp;
            existing.LastMeasurement = hit.RawMeasurement;
            existing.Velocity = hit.TrackVelocity;

            _parentTrackIndex.Update(parentId, existing.LastPosition);

            _logger.LogDebug(
                "Updated parent track {ParentId} position in spatial index",
                parentId);
        }
        else
        {
            // Insert new
            var parentInfo = new ParentTrackInfo
            {
                ParentTrackId = parentId,
                RadarId = hit.RadarId,
                LastPosition = hit.RawMeasurement.ProjectedPosition,
                LastUpdateTime = hit.RawMeasurement.Timestamp,
                FusedTrackId = null,
                LastMeasurement = hit.RawMeasurement,
                State = TrackState.Potential,  // Initial state
                Velocity = hit.TrackVelocity
            };

            _parentTracks[parentId] = parentInfo;
            _parentTrackIndex.Insert(parentId, parentInfo.LastPosition);

            _logger.LogInformation(
                "Added new parent track {ParentId} to spatial index at ({X:F0}, {Y:F0})",
                parentId, parentInfo.LastPosition.X, parentInfo.LastPosition.Y);
        }
    }

    /// <summary>
    /// Updates parent track metadata after a hit is processed.
    /// Shifts LastMeasurement → PreviousMeasurement so both are available for triangulation.
    /// </summary>
    private void UpdateParentTrackMetadata(HitInOverlapZone hit)
    {
        if (_parentTracks.TryGetValue(hit.PerRadarTrackId, out var parentInfo))
        {
            parentInfo.PreviousMeasurement = parentInfo.LastMeasurement;
            parentInfo.LastPosition = hit.RawMeasurement.ProjectedPosition;
            parentInfo.LastUpdateTime = hit.RawMeasurement.Timestamp;
            parentInfo.LastMeasurement = hit.RawMeasurement;
            parentInfo.Velocity = hit.TrackVelocity;
        }
    }

    /// <summary>
    /// Computes position tolerance based on slant range.
    /// Longer ranges → larger tolerance (azimuth errors scale with distance).
    /// At 50km range with 1° error: 50000 * tan(1°) ≈ 873m position error.
    /// </summary>
    private double ComputePositionTolerance(double slantRangeMeters)
    {
        double azimuthErrorRad = _config.AzimuthErrorDegrees * Math.PI / 180.0;
        double positionErrorFromAzimuth = slantRangeMeters * azimuthErrorRad;

        double tolerance = _config.BasePositionToleranceMeters + positionErrorFromAzimuth;

        return Math.Min(tolerance, _config.MaxPositionToleranceMeters);
    }

    /// <summary>
    /// Checks if two tracks have consistent velocities (same target evidence).
    /// Tracks of the same target should have similar speed and heading.
    /// </summary>
    private bool IsVelocityConsistent(Velocity? v1, Velocity? v2)
    {
        // Unknown velocity = neutral (don't reject)
        if (v1 == null || v2 == null)
            return true;

        double speed1 = v1.Magnitude;
        double speed2 = v2.Magnitude;

        // Treat very low velocity as "unestablished" - new tracks often have 0 velocity
        // Don't use unestablished velocity for rejection decisions
        const double unestablishedThreshold = 5.0;
        if (speed1 < unestablishedThreshold || speed2 < unestablishedThreshold)
            return true;

        // Both slow = heading unreliable, accept
        const double minSpeed = 10.0;
        if (speed1 < minSpeed && speed2 < minSpeed)
            return true;

        // Check speed ratio (reject if > 2:1 difference)
        double speedRatio = Math.Max(speed1, speed2) / Math.Min(speed1, speed2);
        if (speedRatio > 2.0)
        {
            _logger.LogDebug(
                "Velocity check failed: speed ratio {Ratio:F1} (speeds {S1:F0} vs {S2:F0} m/s)",
                speedRatio, speed1, speed2);
            return false;
        }

        // Check heading difference (reject if > 60° apart)
        if (speed1 >= minSpeed && speed2 >= minSpeed)
        {
            double heading1 = Math.Atan2(v1.Vy, v1.Vx);
            double heading2 = Math.Atan2(v2.Vy, v2.Vx);

            double headingDiff = Math.Abs(heading1 - heading2);
            if (headingDiff > Math.PI)
                headingDiff = 2 * Math.PI - headingDiff;

            double headingDiffDeg = headingDiff * 180.0 / Math.PI;
            if (headingDiffDeg > 60.0)
            {
                _logger.LogDebug(
                    "Velocity check failed: heading difference {Diff:F0}° exceeds 60°",
                    headingDiffDeg);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks spatial consistency between two measurements using their raw projected positions.
    /// No velocity prediction — each measurement is used at its own timestamp.
    /// The range-dependent tolerance accommodates azimuth errors that scale with distance.
    /// </summary>
    private bool IsSpatiallyConsistent(RawMeasurement m1, RawMeasurement m2)
    {
        // Slant range ratio check
        double minRange = Math.Min(m1.SlantRangeMeters, m2.SlantRangeMeters);
        if (minRange < 100.0)
            return false;

        double maxRange = Math.Max(m1.SlantRangeMeters, m2.SlantRangeMeters);
        double ratio = maxRange / minRange;

        if (ratio > 3.0)
        {
            _logger.LogDebug(
                "Spatial check failed: slant range ratio {Ratio:F1} (SR1={SR1:F0}m, SR2={SR2:F0}m)",
                ratio, m1.SlantRangeMeters, m2.SlantRangeMeters);
            return false;
        }

        double distance = ComputeDistance2D(m1.ProjectedPosition, m2.ProjectedPosition);
        double avgRange = (m1.SlantRangeMeters + m2.SlantRangeMeters) / 2.0;
        double tolerance = ComputePositionTolerance(avgRange);

        if (distance > tolerance)
        {
            _logger.LogDebug(
                "Spatial check failed: distance {Dist:F0}m > tolerance {Tol:F0}m at range {Range:F0}m. " +
                "Pos1=({X1:F0},{Y1:F0}), Pos2=({X2:F0},{Y2:F0})",
                distance, tolerance, avgRange,
                m1.ProjectedPosition.X, m1.ProjectedPosition.Y,
                m2.ProjectedPosition.X, m2.ProjectedPosition.Y);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tries up to three measurement pairings and returns the valid solution with the lowest GDOP.
    /// Pairings attempted (each only if the timestamp delta is within MaxHitTimeDeltaSeconds):
    ///   1. m1Current  vs m2Current
    ///   2. m1Current  vs m2Previous
    ///   3. m1Previous vs m2Current
    /// This avoids velocity prediction: instead of estimating where a track would be at a
    /// different scan time, we find the real hit pair whose timestamps are closest.
    /// </summary>
    private TriangulationSolution? TryBestTriangulation(
        RawMeasurement m1Current,
        RawMeasurement? m1Previous,
        RawMeasurement m2Current,
        RawMeasurement? m2Previous)
    {
        TriangulationSolution? best = null;

        void TryPair(RawMeasurement a, RawMeasurement b)
        {
            double dt = Math.Abs((a.Timestamp - b.Timestamp).TotalSeconds);
            if (dt > _config.MaxHitTimeDeltaSeconds)
                return;

            var sol = _triangulationSolver.Solve3D(a, b);
            if (sol.IsValid && (best == null || sol.GDOP < best.GDOP))
                best = sol;
        }

        TryPair(m1Current,  m2Current);
        if (m2Previous != null) TryPair(m1Current,  m2Previous);
        if (m1Previous != null) TryPair(m1Previous, m2Current);

        return best;
    }

    private static double ComputeDistance2D(Position p1, Position p2)
    {
        double dx = p2.X - p1.X;
        double dy = p2.Y - p1.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Handles radar batch completion. Prunes old parent tracks and updates coasting fused tracks.
    /// </summary>
    public void HandleRadarBatchCompleted(RadarBatchCompleted batch)
    {
        // Prune tracks not associated with a fusion that haven't been seen in several scan cycles.
        var cutoffTime = batch.Timestamp.AddSeconds(-_config.MaxHitTimeDeltaSeconds * 2);
        var staleParents = _parentTracks
            .Where(kvp => kvp.Value.FusedTrackId == null && kvp.Value.LastUpdateTime < cutoffTime)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var parentId in staleParents)
        {
            _parentTracks.Remove(parentId);
            _parentTrackIndex.Delete(parentId);

            _logger.LogDebug("Pruned stale parent track {ParentId}", parentId);
        }

        // Update coasting for fused tracks that didn't get solutions this batch
        var deadTracks = new List<Guid>();

        foreach (var (fusedId, fusedActor) in _fusedTracks)
        {
            if (!_fusedTracksUpdatedThisBatch.Contains(fusedId))
            {
                fusedActor.HandleBatchCompleted(batch.RadarId, batch.Timestamp);

                if (fusedActor.IsDead)
                {
                    deadTracks.Add(fusedId);
                }
            }
        }

        // Clear the batch update tracker
        _fusedTracksUpdatedThisBatch.Clear();

        // Remove dead fused tracks
        foreach (var deadId in deadTracks)
        {
            KillFusedTrack(deadId, "Exceeded miss threshold");
        }

        _logger.LogDebug(
            "Fusion batch completed: {FusedTracks} fused tracks, {ParentTracks} parent tracks in index",
            _fusedTracks.Count, _parentTracks.Count);
    }

    /// <summary>
    /// Handles parent track death events from CentralTrackManager.
    /// Cleans up fused tracks and spatial index entries.
    /// </summary>
    public void HandleParentTrackDied(ParentTrackDied evt)
    {
        _logger.LogInformation(
            "Parent track {ParentId} from radar {RadarId} died with state {State}",
            evt.ParentTrackId, evt.RadarId, evt.FinalState);

        // Check if parent was fused
        if (_parentToFusedMap.TryGetValue(evt.ParentTrackId, out var fusedId))
        {
            // USER REQUIREMENT: If either parent is DEAD, kill fused track immediately
            if (evt.FinalState == TrackState.Dead)
            {
                KillFusedTrack(fusedId, $"Parent {evt.ParentTrackId} died");
            }
        }

        // Clean up parent track metadata
        _parentTracks.Remove(evt.ParentTrackId);
        _parentTrackIndex.Delete(evt.ParentTrackId);
        _parentToFusedMap.Remove(evt.ParentTrackId);
    }

    /// <summary>
    /// Handles parent track state changes from CentralTrackManager.
    /// Synchronizes fused track state with parent track states.
    /// </summary>
    public void HandleParentTrackStateChanged(ParentTrackStateChanged evt)
    {
        // Update parent track metadata
        if (_parentTracks.TryGetValue(evt.ParentTrackId, out var parentInfo))
        {
            parentInfo.State = evt.NewState;
        }

        // Check if this parent is part of a fused track
        if (!_parentToFusedMap.TryGetValue(evt.ParentTrackId, out var fusedId))
            return;

        if (!_fusedTracks.TryGetValue(fusedId, out var fusedActor))
            return;

        var parentTracks = _fusedToParentsMap[fusedId];

        // Transition fused track to coasting if a constituent radar track loses its lock.
        if (evt.NewState == TrackState.Coasting)
        {
            fusedActor.EnterCoastingMode();
            _logger.LogInformation(
                "Fused track {FusedId} entering coasting due to parent {ParentId} coasting",
                fusedId, evt.ParentTrackId);
        }

        // Check if both parents are now VERIFIED (re-acquisition)
        var allParentsVerified = parentTracks
            .All(pid => _parentTracks.TryGetValue(pid, out var p) && p.State == TrackState.Verified);

        if (allParentsVerified && fusedActor.State == FusedTrackState.Coasting)
        {
            _logger.LogInformation(
                "Fused track {FusedId} ready to resume - both parents verified",
                fusedId);
            // Fused track will resume confirmed status upon the next successful triangulation update.
        }
    }

    /// <summary>
    /// Kills a fused track and cleans up all references.
    /// </summary>
    private void KillFusedTrack(Guid fusedId, string reason)
    {
        if (!_fusedTracks.TryGetValue(fusedId, out var fusedActor))
            return;

        _logger.LogInformation("Killing fused track {FusedId}: {Reason}", fusedId, reason);

        // Mark as dead/lost
        fusedActor.MarkAsDead();

        // Remove from all tracking structures
        _fusedTracks.Remove(fusedId);

        // Clean up parent mappings
        if (_fusedToParentsMap.TryGetValue(fusedId, out var parents))
        {
            foreach (var parentId in parents)
            {
                _parentToFusedMap.Remove(parentId);

                // Re-add parents to the spatial index so they can attempt new fusions.
                if (_parentTracks.TryGetValue(parentId, out var parentInfo))
                {
                    parentInfo.FusedTrackId = null;
                    _parentTrackIndex.Insert(parentId, parentInfo.LastPosition);
                }
            }
            _fusedToParentsMap.Remove(fusedId);
        }
    }

    /// <summary>
    /// Gets the total number of active fused tracks.
    /// </summary>
    public int FusedTrackCount => _fusedTracks.Count;

    /// <summary>
    /// Gets a fused track by ID, if it exists.
    /// </summary>
    public FusedTrack? GetFusedTrack(Guid fusedTrackId)
    {
        return _fusedTracks.TryGetValue(fusedTrackId, out var actor)
            ? actor.GetFusedTrack()
            : null;
    }

    /// <summary>
    /// Gets all active fused tracks.
    /// </summary>
    public IEnumerable<FusedTrack> GetAllFusedTracks()
    {
        return _fusedTracks.Values.Select(a => a.GetFusedTrack());
    }
}
