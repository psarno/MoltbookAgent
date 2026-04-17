using MoltbookAgent;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);

if (OperatingSystem.IsWindows())
    builder.Services.AddWindowsService(options => { options.ServiceName = "MoltbookAgent"; });
else
    builder.Services.AddSystemd();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// Windows SCM does not set the working directory to the binary directory — it uses
// C:\Windows\System32 by default. Normalize it here so relative paths in config.toml
// resolve correctly whether running interactively (dotnet run) or as a Windows service.
if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

host.Run();
