// Program.cs — DI registration excerpt (performance evaluation relevant section only)
// Full Program.cs is part of the private capstone repository.

using aerial_monitoring_system.Actors;
using aerial_monitoring_system.Infrastructure;
using aerial_monitoring_system.Infrastructure.Metrics;
using aerial_monitoring_system.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// ... (SignalR, CORS, and other infrastructure registrations omitted) ...

// Register infrastructure services
builder.Services.AddSingleton<IEventBus, EventBus>();
builder.Services.AddSingleton<IOverlapZoneDetector, OverlapZoneDetector>();
builder.Services.AddSingleton<ITriangulationSolver, TriangulationSolver>();
builder.Services.AddSingleton<ILatencyMetricsService, LatencyMetricsService>();

// Register algorithm services
builder.Services.AddSingleton<IGatingAlgorithm, GatingAlgorithm>();
builder.Services.AddSingleton<IGlobalNearestNeighbor, GlobalNearestNeighbor>();
builder.Services.AddSingleton<IAltitudeEstimator, AltitudeEstimator>();
builder.Services.AddTransient<IKalmanFilter, KalmanFilter>();

// Register actor/manager services
builder.Services.AddSingleton<CentralTrackManager>();
builder.Services.AddSingleton<ITrackActorFactory, TrackActorFactory>();
builder.Services.AddSingleton<FusionTrackManager>();
builder.Services.AddSingleton<SignalRHubActor>();

// Register radar-relay client as hosted service
builder.Services.AddHostedService<RadarRelayClient>();

// Register metrics background services — these run for the duration of the process
builder.Services.AddHostedService<GcMetricsService>();
builder.Services.AddHostedService<SystemMetricsService>();

var app = builder.Build();

// Force singletons to instantiate so they subscribe to the event bus
app.Services.GetRequiredService<CentralTrackManager>();
app.Services.GetRequiredService<FusionTrackManager>();
app.Services.GetRequiredService<SignalRHubActor>();
