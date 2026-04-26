using SMMonitor.Common;

namespace SMMonitor.Agent.Service;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SMMonitorAgent service started.");

        AgentConfigStore.SaveStatus(new AgentStatus
        {
            ServiceRunning = true,
            WsConnected = false,
            LastUploadTime = DateTime.Now,
            LastError = "service starting"
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            AgentSettings settings;

            try
            {
                settings = AgentConfigStore.Load();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Load config failed.");

                AgentConfigStore.SaveStatus(new AgentStatus
                {
                    ServiceRunning = true,
                    WsConnected = false,
                    LastError = "load config failed: " + ex.Message,
                    LastUploadTime = DateTime.Now
                });

                await SafeDelayAsync(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            if (!settings.EnableUpload)
            {
                AgentConfigStore.SaveStatus(new AgentStatus
                {
                    ClientId = settings.ClientId,
                    ServiceRunning = true,
                    WsConnected = false,
                    LastError = "upload disabled",
                    LastUploadTime = DateTime.Now,
                    ServerUrl = settings.ServerUrl
                });

                await SafeDelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            try
            {
                var agent = new WsMonitorAgent(settings, _logger);
                await agent.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent run failed.");

                AgentConfigStore.SaveStatus(new AgentStatus
                {
                    ClientId = settings.ClientId,
                    ServiceRunning = true,
                    WsConnected = false,
                    LastError = ex.Message,
                    LastUploadTime = DateTime.Now,
                    ServerUrl = settings.ServerUrl
                });
            }

            await SafeDelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
        }

        AgentConfigStore.SaveStatus(new AgentStatus
        {
            ServiceRunning = false,
            WsConnected = false,
            LastUploadTime = DateTime.Now,
            LastError = "service stopped"
        });

        _logger.LogInformation("SMMonitorAgent service stopped.");
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
