using System.IO.Pipes;
using System.Text;
using System.Text.Json;

const string pipeName = "SMMONITOR_PIPE_7f8e5fd8d6f24f7fabf4b1291bc03a3d";
var appName = args.Length > 0 ? args[0] : "DemoApp";

Console.WriteLine($"[Demo] pipe={pipeName}, app={appName}");
Console.WriteLine("[Demo] Press Ctrl+C to stop.");

var i = 0;
while (true)
{
    try
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);

        var payload = new
        {
            app = appName,
            level = i % 10 == 0 ? "warn" : "info",
            message = $"heartbeat #{i}",
            queue = Random.Shared.Next(0, 200),
            workers = Random.Shared.Next(1, 9),
            ts = DateTimeOffset.Now.ToUnixTimeSeconds()
        };

        var line = JsonSerializer.Serialize(payload) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await client.WriteAsync(bytes);
        await client.FlushAsync();

        Console.WriteLine($"[Demo] sent: {line.Trim()}");
        i++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Demo] send failed: {ex.Message}");
    }

    await Task.Delay(1000);
}
