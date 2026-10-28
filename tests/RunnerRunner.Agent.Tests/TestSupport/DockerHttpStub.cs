using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RunnerRunner.Agent.Tests.TestSupport;

/// <summary>A real per-test HTTP endpoint for Docker.DotNet, never a process-wide DOCKER_HOST.</summary>
internal sealed class DockerHttpStub : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopped = new(TimeSpan.FromSeconds(30));
    private readonly Task serving;
    public List<JsonElement> CreateBodies { get; } = [];
    public List<string> Requests { get; } = [];
    public string ImageOs { get; set; } = "linux";
    /// <summary>Labels returned by GET /containers/{id}/json.</summary>
    public Dictionary<string, string> ContainerLabels { get; } = [];
    public Uri Endpoint { get; }

    public DockerHttpStub()
    {
        listener.Start();
        Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        serving = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                using var connection = await listener.AcceptTcpClientAsync(stopped.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(stopped.Token) ?? "";
                Requests.Add(requestLine);
                var length = 0;
                while (await reader.ReadLineAsync(stopped.Token) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(header["Content-Length:".Length..].Trim());
                    if (header.Equals("Expect: 100-continue", StringComparison.OrdinalIgnoreCase))
                        await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), stopped.Token);
                }

                var body = new char[length];
                if (length > 0)
                    Assert.Equal(length, await reader.ReadBlockAsync(body.AsMemory(), stopped.Token));
                var status = "200 OK";
                var response = JsonSerializer.Serialize(new { Os = ImageOs, Config = new { WorkingDir = "/misleading-image-cwd", User = "1001" } });
                if (requestLine.Contains("/containers/create", StringComparison.Ordinal))
                {
                    CreateBodies.Add(JsonDocument.Parse(new string(body)).RootElement.Clone());
                    status = "201 Created";
                    response = """{"Id":"0123456789abcdef","Warnings":[]}""";
                }
                else if (requestLine.Contains("/start", StringComparison.Ordinal)
                    || requestLine.Contains("/stop", StringComparison.Ordinal)
                    || requestLine.StartsWith("DELETE /", StringComparison.Ordinal))
                {
                    status = "204 No Content";
                    response = "";
                }
                else if (requestLine.StartsWith("GET /", StringComparison.Ordinal) && requestLine.Contains("/containers/", StringComparison.Ordinal))
                {
                    response = JsonSerializer.Serialize(new
                    {
                        Id = "0123456789abcdef",
                        State = new { Running = true, Status = "running" },
                        Config = new { Labels = ContainerLabels }
                    });
                }
                else if (!requestLine.Contains("/images/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unexpected Docker request: {requestLine}");
                }

                var bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), stopped.Token);
                await stream.WriteAsync(bytes, stopped.Token);
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await stopped.CancelAsync();
        listener.Stop();
        await serving;
        stopped.Dispose();
    }
}
