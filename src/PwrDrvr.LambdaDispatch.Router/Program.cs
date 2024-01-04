using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace PwrDrvr.LambdaDispatch.Router;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine($"Before ThreadPool ThreadCount: {ThreadPool.ThreadCount}");
        ThreadPool.SetMinThreads(20, 20);
        Console.WriteLine($"After ThreadPool ThreadCount: {ThreadPool.ThreadCount}");
        await GetCallbackIP.Get().ConfigureAwait(false);
        CreateHostBuilder(args).Build().Run();
    }

    public static string GetCertPath(string filename)
    {
        // Check if the 'certs/' folder is in the current directory
        if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "certs")))
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "certs", filename);
        }

        // Check if the 'certs/' folder is two directories up
        var twoDirectoriesUp = Directory.GetParent(Directory.GetParent(Directory.GetCurrentDirectory()).FullName).FullName;
        if (Directory.Exists(Path.Combine(twoDirectoriesUp, "certs")))
        {
            return Path.Combine(twoDirectoriesUp, "certs", filename);
        }

        // If the 'certs/' folder is not found, throw
        throw new Exception("Could not find the 'certs/' folder");
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                // Add other configuration sources as needed
                // config.AddJsonFile("appsettings.json", optional: true);
                config.AddEnvironmentVariables(prefix: "LAMBDA_DISPATCH_");
            })
            .ConfigureServices((hostContext, services) =>
            {
                var config = Config.CreateAndValidate(hostContext.Configuration);
                services.AddSingleton<IConfig>(config);
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddAWSProvider();
                logging.AddSimpleConsole(options =>
                {
                    // options.IncludeScopes = true;
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss.fff ";
                });
#if DEBUG
                logging.SetMinimumLevel(LogLevel.Debug); // Set the minimum log level here
#else
                logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
                logging.SetMinimumLevel(LogLevel.Information); // Set the minimum log level here
#endif
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                // 5002 - incoming requests
                // 5001 - lambda interface HTTP
                // 5003 - lambda interface HTTPS
                // webBuilder.UseUrls("http://0.0.0.0:5002", "http://0.0.0.0:5001", "https://0.0.0.0:5003");
                webBuilder.ConfigureKestrel((context, serverOptions) =>
                {
                    // We have to reparse the config once, bummer
                    var config = Config.CreateAndValidate(context.Configuration);
#if USE_INSECURE_HTTP2
                    serverOptions.ListenLocalhost(config.ControlChannelHTTPPort, o => o.Protocols = HttpProtocols.Http2);
#endif
                    serverOptions.ListenAnyIP(config.IncomingRequestHTTPPort);
                    serverOptions.ListenAnyIP(config.ControlChannelHTTP2Port, listenOptions =>
                    {
                        listenOptions.UseHttps(GetCertPath("lambdadispatch.local.pfx"));
                    });
                });
                webBuilder.UseStartup<Startup>();
            });
}