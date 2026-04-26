using System.IO.Pipes;
using System.Text;

namespace SMMonitor.Agent.Service;

public sealed class AppPipeForwarder
{
    private readonly string _pipeName;
    private readonly ILogger _logger;

    public AppPipeForwarder(string pipeName, ILogger logger)
    {
        _pipeName = pipeName;
        _logger = logger;
    }

    public async Task RunAsync(Func<AppPipeMessage, Task> onMessage, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                while (!token.IsCancellationRequested && server.IsConnected)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (line == null)
                    {
                        break;
                    }

                    var msg = new AppPipeMessage
                    {
                        PipeName = _pipeName,
                        Content = line,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    await onMessage(msg);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pipe forward loop error. Pipe={PipeName}", _pipeName);
                try
                {
                    await Task.Delay(500, token);
                }
                catch
                {
                    break;
                }
            }
        }
    }
}

public sealed class AppPipeMessage
{
    public string PipeName { get; set; } = "";
    public string Content { get; set; } = "";
    public long Timestamp { get; set; }
}
