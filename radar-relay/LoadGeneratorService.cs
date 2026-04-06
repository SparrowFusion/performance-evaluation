using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using RadarDataServer.Hubs;

namespace RadarDataServer.Services;

/// <summary>
/// Generates synthetic radar detections at a configurable rate and pattern for load testing.
/// Enabled via LoadGenerator:Enabled in appsettings.json.
/// Runs alongside RadarBroadcastService; when the data folder is missing RadarBroadcastService
/// exits early, so only LoadGeneratorService produces traffic.
/// </summary>
public class LoadGeneratorService : BackgroundService
{
    private readonly IHubContext<RadarDataHub> _hubContext;
    private readonly ILogger<LoadGeneratorService> _logger;
    private readonly LoadGeneratorConfig _config;

    // These must match the RadarSystem:Radars RadarId values in AMS appsettings.json
    // so that CentralTrackManager can look up radar config and fire overlap zone events.
    private static readonly Guid Radar1Id = new("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Radar2Id = new("00000000-0000-0000-0000-000000000030");

    // Midpoint between the two configured radars — targets here are visible to both.
    // Radar1: (51.1134, -114.0428), Radar2: (51.1105, -114.3735) → midpoint ~(-114.208, 51.112)
    private const double OriginLat = 51.112;
    private const double OriginLon = -114.208;
    private const double DegreesPerKm = 1.0 / 111.0;

    private readonly Random _rng = new();

    public LoadGeneratorService(
        IHubContext<RadarDataHub> hubContext,
        ILogger<LoadGeneratorService> logger,
        IConfiguration configuration)
    {
        _hubContext = hubContext;
        _logger = logger;
        _config = configuration.GetSection("LoadGenerator").Get<LoadGeneratorConfig>()
                  ?? new LoadGeneratorConfig();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("LoadGeneratorService is disabled");
            return;
        }

        _logger.LogInformation(
            "LoadGeneratorService starting: rate={Rate}/s, pattern={Pattern}, targets={Targets}, duration={Duration}s",
            _config.RatePerSecond, _config.Pattern, _config.TargetCount, _config.DurationSeconds);

        await Task.Delay(1500, stoppingToken); // let server fully start

        using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (_config.DurationSeconds > 0)
            durationCts.CancelAfter(TimeSpan.FromSeconds(_config.DurationSeconds));

        var token = durationCts.Token;
        var elapsed = Stopwatch.StartNew();

        while (!token.IsCancellationRequested)
        {
            double currentRate = ComputeCurrentRate(elapsed.Elapsed.TotalSeconds);

            if (currentRate <= 0)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                continue;
            }

            var delayMs = (int)(1000.0 / currentRate);

            await BroadcastBatch(stoppingToken);
            _sent++;

            if (_sent % 50 == 0)
                _logger.LogInformation("LoadGenerator: {Sent} batches sent, current rate={Rate:F1}/s", _sent, currentRate);

            try { await Task.Delay(Math.Max(1, delayMs), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("LoadGeneratorService finished after {Sent} batches", _sent);
    }

    private double ComputeCurrentRate(double elapsedSeconds)
    {
        return _config.Pattern switch
        {
            "Spiked" => IsSpikeWindow(elapsedSeconds) ? _config.RatePerSecond * 10 : _config.RatePerSecond / 2.0,
            "Decreased" => _config.DurationSeconds > 0
                ? Math.Max(0, _config.RatePerSecond * (1.0 - elapsedSeconds / _config.DurationSeconds))
                : _config.RatePerSecond,
            _ => _config.RatePerSecond // Steady
        };
    }

    // Spike every 10 seconds for a 2-second burst
    private static bool IsSpikeWindow(double elapsedSeconds) =>
        elapsedSeconds % 10.0 < 2.0;

    private async Task BroadcastBatch(CancellationToken stoppingToken)
    {
        var radarId = _sent % 2 == 0 ? Radar1Id : Radar2Id;
        var radarName = radarId == Radar1Id ? "LoadGen-Radar1" : "LoadGen-Radar2";

        var observations = Enumerable.Range(0, _config.ObservationsPerBatch)
            .Select(_ => MakeObservation())
            .ToList();

        var radarData = new
        {
            Source = new
            {
                SensorType = "Sparrowhawk",
                Name = radarName,
                Identifier = radarId
            },
            Observations = observations
        };

        var message = new
        {
            Source = radarName,
            FileName = $"synthetic-{DateTime.UtcNow:HHmmss-fff}.json",
            Timestamp = DateTime.UtcNow,
            Data = JsonSerializer.Serialize(radarData)
        };

        await _hubContext.Clients.All.SendAsync("ReceiveRadarData", message, stoppingToken);
    }

    private long _sent = 0; // used in BroadcastBatch to alternate radars

    private object MakeObservation()
    {
        // Pick a random target index and give it a slightly drifting position
        int targetIdx = _rng.Next(_config.TargetCount);
        double bearingDeg = targetIdx * (360.0 / _config.TargetCount);
        // Keep targets within ~10 km of midpoint so they stay inside both radars' 26 km range
        double rangeKm = 3.0 + targetIdx * 1.5 + _rng.NextDouble() * 1.0;

        double lat = OriginLat + Math.Cos(bearingDeg * Math.PI / 180.0) * rangeKm * DegreesPerKm;
        double lon = OriginLon + Math.Sin(bearingDeg * Math.PI / 180.0) * rangeKm * DegreesPerKm;

        return new
        {
            LatitudeDeg = lat + (_rng.NextDouble() - 0.5) * 0.001,
            LongitudeDeg = lon + (_rng.NextDouble() - 0.5) * 0.001,
            AltitudeFt = (object?)null,
            Time = DateTime.UtcNow,
            Intensity = 30.0 + _rng.NextDouble() * 40.0,
            ArcDeg = 1.0,
            DepthM = 0.0,
            DistanceFromRadarNmi = rangeKm * 0.539957,
            BearingFromRadarDeg = bearingDeg
        };
    }
}

public class LoadGeneratorConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Batches (radar scan groups) per second.</summary>
    public double RatePerSecond { get; set; } = 10;

    /// <summary>Steady | Spiked | Decreased</summary>
    public string Pattern { get; set; } = "Steady";

    /// <summary>How long to run. 0 = run until cancelled.</summary>
    public int DurationSeconds { get; set; } = 0;

    /// <summary>Number of synthetic targets to simulate.</summary>
    public int TargetCount { get; set; } = 5;

    /// <summary>Observations per batch (simulates detections per scan).</summary>
    public int ObservationsPerBatch { get; set; } = 1;
}
