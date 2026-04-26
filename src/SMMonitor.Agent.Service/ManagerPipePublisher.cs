using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SMMonitor.Common;

namespace SMMonitor.Agent.Service;

public static class ManagerPipePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task TryPublishAsync(string category, string source, string message, CancellationToken token)
    {
        try
        {
            var pipeName = AgentSettings.ManagerPipeName;
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(300));
            await client.ConnectAsync(cts.Token);

            var payload = JsonSerializer.Serialize(new ManagerPipePayload
            {
                Timestamp = DateTime.Now,
                Category = category,
                Source = source,
                Message = message
            }, JsonOptions);

            var bytes = Encoding.UTF8.GetBytes(payload + "\n");
            await client.WriteAsync(bytes, cts.Token);
            await client.FlushAsync(cts.Token);
        }
        catch
        {
            // manager may be offline
        }
    }
}

public sealed class ManagerPipePayload
{
    public DateTime Timestamp { get; set; }
    public string Category { get; set; } = "Service";
    public string Source { get; set; } = "Agent";
    public string Message { get; set; } = "";
}
