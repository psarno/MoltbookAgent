using MoltbookAgent;

var builder = Host.CreateApplicationBuilder(args);

if (OperatingSystem.IsWindows())
    builder.Services.AddWindowsService(options => { options.ServiceName = "MoltbookAgent"; });
else
    builder.Services.AddSystemd();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
