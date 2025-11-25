using Grpc.Core;
using Grpc.Net.Client;
using SentinAI.Shared;

namespace SentinAI.Web.Services;

public interface ISentinelClient
{
    Task<bool> ConnectAsync();
    Task<ServiceStatus> GetStatusAsync();
    Task<CleanupResult> ExecuteCleanupAsync(List<string> filePaths, string analysisId, bool userApproved);
    bool IsConnected { get; }
}

/// <summary>
/// gRPC client for communicating with the Sentinel Service
/// </summary>
public class SentinelClient : ISentinelClient, IDisposable
{
    private GrpcChannel? _channel;
    private AgentService.AgentServiceClient? _client;

    public bool IsConnected => _channel != null && _client != null;

    public async Task<bool> ConnectAsync()
    {
        try
        {
            // Connect to Named Pipe endpoint
            _channel = GrpcChannel.ForAddress(
                "http://localhost",
                new GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler
                    {
                        ConnectCallback = async (context, cancellationToken) =>
                        {
                            var socket = new System.Net.Sockets.Socket(
                                System.Net.Sockets.AddressFamily.Unix,
                                System.Net.Sockets.SocketType.Stream,
                                System.Net.Sockets.ProtocolType.Unspecified);

                            var endpoint = new System.Net.Sockets.UnixDomainSocketEndPoint(
                                @"\\.\pipe\sentinai-agent-pipe");

                            await socket.ConnectAsync(endpoint, cancellationToken);
                            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                        }
                    }
                });

            _client = new AgentService.AgentServiceClient(_channel);

            // Test connection
            var status = await GetStatusAsync();
            return status.IsRunning;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to connect to Sentinel Service: {ex.Message}");
            return false;
        }
    }

    public async Task<ServiceStatus> GetStatusAsync()
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Not connected to service");
        }

        var request = new StatusRequest { Detailed = true };
        return await _client.GetServiceStatusAsync(request);
    }

    public async Task<CleanupResult> ExecuteCleanupAsync(
        List<string> filePaths,
        string analysisId,
        bool userApproved)
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Not connected to service");
        }

        var request = new CleanupCommand
        {
            AnalysisId = analysisId,
            UserApproved = userApproved
        };
        request.FilePaths.AddRange(filePaths);

        return await _client.ExecuteCleanupAsync(request);
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }
}
