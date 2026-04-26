using System.IO.Pipes;
using System.Text;
using System.Text.Json;

const string appPipe = "SMMONITOR_PIPE_7f8e5fd8d6f24f7fabf4b1291bc03a3d";
const string managerPipe = "SMMANAGER_PIPE_7f8e5fd8d6f24f7fabf4b1291bc03a3d";

var mode = "app";
var appName = "DemoApp";
var pipeName = appPipe;

for (var idx = 0; idx < args.Length; idx++)
{
    var arg = args[idx];
    switch (arg.ToLowerInvariant())
    {
        case "--mode":
            mode = idx + 1 < args.Length ? args[++idx].ToLowerInvariant() : mode;
            break;
        case "--pipe":
            pipeName = idx + 1 < args.Length ? args[++idx] : pipeName;
            break;
        case "--app":
            appName = idx + 1 < args.Length ? args[++idx] : appName;
            break;
    }
}

if (pipeName == appPipe && mode == "manager")
{
    pipeName = managerPipe;
}

Console.WriteLine($"[Demo] mode={mode}, pipe={pipeName}, app={appName}");

if (mode == "capture")
{
    await SendCaptureRequestAsync(pipeName);
    return;
}

Console.WriteLine("[Demo] Press Ctrl+C to stop.");
var i = 0;
while (true)
{
    try
    {
        await SendLogLineAsync(pipeName, mode, appName, i);
        i++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Demo] send failed: {ex.Message}");
    }

    await Task.Delay(1000);
}

static async Task SendLogLineAsync(string pipeName, string mode, string appName, int i)
{
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
    await client.ConnectAsync(3000);

    object payload = mode == "manager"
        ? new
        {
            timestamp = DateTime.Now,
            category = "ThirdParty",
            source = appName,
            message = $"manager-log heartbeat #{i}"
        }
        : new
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
}

static async Task SendCaptureRequestAsync(string pipeName)
{
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await client.ConnectAsync(3000);
    using var reader = new StreamReader(client, Encoding.UTF8, true, leaveOpen: true);
    await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

    var request = JsonSerializer.Serialize(new
    {
        action = "capture_screen",
        imageFormat = "jpeg",
        quality = 70
    });

    await writer.WriteLineAsync(request);
    var line = await reader.ReadLineAsync();
    Console.WriteLine($"[Demo] capture response: {line}");
}
