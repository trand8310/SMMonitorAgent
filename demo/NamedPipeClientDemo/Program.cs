using System.IO.Pipes;
using System.Text;
using System.Text.Json;

const string appPipe = "SMMONITOR_PIPE_7f8e5fd8d6f24f7fabf4b1291bc03a3d";
var appName = "DemoApp";
var pipeName = appPipe;
var intervalMs = 1000;
var count = 0; // 0 表示无限发送

for (var idx = 0; idx < args.Length; idx++)
{
    var arg = args[idx];
    switch (arg.ToLowerInvariant())
    {
        case "--pipe":
            pipeName = idx + 1 < args.Length ? args[++idx] : pipeName;
            break;
        case "--app":
            appName = idx + 1 < args.Length ? args[++idx] : appName;
            break;
        case "--interval":
            if (idx + 1 < args.Length && int.TryParse(args[++idx], out var iv))
            {
                intervalMs = Math.Clamp(iv, 100, 60000);
            }
            break;
        case "--count":
            if (idx + 1 < args.Length && int.TryParse(args[++idx], out var c))
            {
                count = Math.Max(0, c);
            }
            break;
    }
}

Console.WriteLine($"[Demo] pipe={pipeName}, app={appName}, intervalMs={intervalMs}, count={(count == 0 ? "∞" : count)}");
Console.WriteLine("[Demo] 该 DEMO 为单向发送：仅向 Agent 管道写入消息，不处理任何回包。");
Console.WriteLine("[Demo] 若网页订阅不到消息，请先在控制台对目标客户端开启“实时推送( pipe_live_push )”。");

Console.WriteLine("[Demo] Press Ctrl+C to stop.");
var i = 0;
while (count == 0 || i < count)
{
    try
    {
        await SendLogLineAsync(pipeName, appName, i);
        i++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Demo] send failed: {ex.Message}");
    }

    await Task.Delay(intervalMs);
}

static async Task SendLogLineAsync(string pipeName, string appName, int i)
{
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
    await client.ConnectAsync(3000);

    var payload = new
    {
        app = appName,
        appName, // 兼容不同侧解析
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
