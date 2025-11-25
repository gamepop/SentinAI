using System.IO.Pipes;
using System.Net.Http;
using Grpc.Net.Client;
using SentinAI.Shared;
using SentinAI.Web.Components;
using SentinAI.Web.Hubs;
using SentinAI.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Telemetry & diagnostics
builder.Services.AddApplicationInsightsTelemetry();

// HttpClient factory for API calls from server-side components
builder.Services.AddHttpClient();

// Add controllers for API endpoints
builder.Services.AddControllers();

// Add gRPC services
builder.Services.AddGrpc();

// Add SignalR for real-time updates
builder.Services.AddSignalR();
builder.Services.AddSingleton<IAgentNotificationService, AgentNotificationService>();
builder.Services.AddSingleton<IMonitoringActivityBroadcaster, MonitoringActivityBroadcaster>();
builder.Services.AddSingleton<IMonitoringActivityService, MonitoringActivityService>();
builder.Services.AddHostedService<MonitoringActivityWatcher>();

// Configure Brain settings from appsettings.json
builder.Services.Configure<BrainConfiguration>(
    builder.Configuration.GetSection(BrainConfiguration.SectionName));

// Add Brain services
builder.Services.AddSingleton<IAgentBrain, AgentBrain>();
builder.Services.AddSingleton<IWinapp2Parser, Winapp2Parser>();
builder.Services.AddSingleton<IModelDownloadService, ModelDownloadService>();

// Add configuration manager
builder.Services.AddSingleton<SentinAI.Shared.Services.IConfigurationManager, SentinAI.Shared.Services.ConfigurationManager>();

// Add gRPC client to connect to Sentinel Service via Named Pipe
builder.Services.AddSingleton(sp =>
{
    var handler = new SocketsHttpHandler
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            var stream = new NamedPipeClientStream(
                serverName: ".",
                pipeName: "sentinai-agent-pipe",
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

// Initialize Brain on startup
builder.Services.AddHostedService<BrainInitializationService>();

// Configure Kestrel to listen on Named Pipe for gRPC (Brain service)
builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP endpoint for web UI
    options.ListenLocalhost(5203, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2);

    // Named Pipe for gRPC communication with Sentinel Service
    options.ListenNamedPipe("sentinai-brain-pipe", listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();

app.UseAntiforgery();

app.UseStaticFiles();

// Map API controllers
app.MapControllers();

// Map gRPC Brain service
app.MapGrpcService<BrainGrpcService>();

// Map SignalR hub
app.MapHub<AgentHub>("/hubs/agent");

// Map Razor components
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
