using Grpc.Net.Client;
using SentinAI.SentinelService;
using SentinAI.SentinelService.Services;
using SentinAI.Shared;
using SentinAI.Shared.Services;
using Serilog;
using Serilog.Events;
using System.IO.Pipes;
using System.Net.Http;
using System.IO;

// Configure Serilog
var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logDirectory, "sentinai-service-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true)
    .WriteTo.EventLog("SentinAI", manageEventSource: true, restrictedToMinimumLevel: LogEventLevel.Warning)
    .CreateLogger();

try
{
    Log.Information("Starting SentinAI Sentinel Service");

    var builder = WebApplication.CreateBuilder(args);

    // Configure Windows Service hosting
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "SentinAI Sentinel Service";
    });

    builder.Host.UseSerilog();

    // Register configuration manager
    builder.Services.AddSingleton<SentinAI.Shared.Services.IConfigurationManager, SentinAI.Shared.Services.ConfigurationManager>();

    // Monitoring activity infrastructure
    builder.Services.AddSingleton<IMonitoringActivityStore, MonitoringActivityStore>();
    builder.Services.AddSingleton<IMonitoringActivityPublisher, MonitoringActivityPublisher>();

    // Brain gRPC client (connects to web-hosted Brain via named pipe)
    builder.Services.AddSingleton(_ =>
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var stream = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: "sentinai-brain-pipe",
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);

                await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return stream;
            }
        };

        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });

        return new AgentService.AgentServiceClient(channel);
    });

    // Register core services
    builder.Services.AddHostedService<DriveMonitor>();
    builder.Services.AddSingleton<IUsnJournalReader, UsnJournalReader>();
    builder.Services.AddSingleton<ICleanupExecutor, CleanupExecutor>();
    builder.Services.AddSingleton<IStateMachineOrchestrator, StateMachineOrchestrator>();

    // Register gRPC service
    builder.Services.AddGrpc();
    builder.Services.AddSingleton<AgentServiceImpl>();

    // Configure Kestrel to use Named Pipe for IPC with HTTP/2 for gRPC
    builder.WebHost.ConfigureKestrel(options =>
    {
        // Use Named Pipe for IPC - must be HTTP/2 only for gRPC
        options.ListenNamedPipe("sentinai-agent-pipe", listenOptions =>
        {
            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
        });
    });

    var app = builder.Build();

    // Map gRPC service endpoint
    app.MapGrpcService<AgentServiceImpl>();

    // Run the application
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;
