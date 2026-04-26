using SMMonitor.Agent.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "SMMonitorAgent";
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
