using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace PwrDrvr.LambdaDispatch.Router;

public class Program
{
    /// <summary>
    /// Adjust the ThreadPool settings based on environment variables
    /// LAMBDA_DISPATCH_MinWorkerThreads
    /// LAMBDA_DISPATCH_MinCompletionPortThreads
    /// LAMBDA_DISPATCH_MaxWorkerThreads
    /// LAMBDA_DISPATCH_MaxCompletionPortThreads
    /// 
    /// Related to: DOTNET_ThreadPool_UnfairSemaphoreSpinLimit
    /// </summary>
    /// <exception cref="Exception"></exception>
    public static void AdjustThreadPool()
    {
        // Get the default min and max number of worker and completionport threads
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxCompletionPortThreads);

        Console.WriteLine($"Default min threads: {minWorkerThreads} worker threads, {minCompletionPortThreads} completion port threads");
        Console.WriteLine($"Default max threads: {maxWorkerThreads} worker threads, {maxCompletionPortThreads} completion port threads");

        // Check for LAMBDA_DISPATCH_MaxWorkerThreads and LAMBDA_DISPATCH_MaxCompletionPortThreads and apply those new max limits if set
        var maxWorkerThreadsEnv = Environment.GetEnvironmentVariable("LAMBDA_DISPATCH_MaxWorkerThreads");
        var maxCompletionPortThreadsEnv = Environment.GetEnvironmentVariable("LAMBDA_DISPATCH_MaxCompletionPortThreads");
        if (int.TryParse(maxWorkerThreadsEnv, out var newMaxWorkerThreads))
        {
            maxWorkerThreads = newMaxWorkerThreads;
        }
        if (int.TryParse(maxCompletionPortThreadsEnv, out var newMaxCompletionPortThreads))
        {
            maxCompletionPortThreads = newMaxCompletionPortThreads;
        }

        // Ensure min is less than or equal to max in the case where min/max are not overridden
        minWorkerThreads = Math.Min(minWorkerThreads, maxWorkerThreads);
        minCompletionPortThreads = Math.Min(minCompletionPortThreads, maxCompletionPortThreads);

        // Override the calculated min value if set in an env var
        var minWorkerThreadsEnv = Environment.GetEnvironmentVariable("LAMBDA_DISPATCH_MinWorkerThreads");
        var minCompletionPortThreadsEnv = Environment.GetEnvironmentVariable("LAMBDA_DISPATCH_MinCompletionPortThreads");
        if (int.TryParse(minWorkerThreadsEnv, out var newMinWorkerThreads))
        {
            minWorkerThreads = newMinWorkerThreads;
        }
        if (int.TryParse(minCompletionPortThreadsEnv, out var newMinCompletionPortThreads))
        {
            minCompletionPortThreads = newMinCompletionPortThreads;
        }

        var setMinResult = ThreadPool.SetMinThreads(minWorkerThreads, minCompletionPortThreads);
        if (!setMinResult)
        {
            throw new Exception($"Failed to set min threads to {minWorkerThreads} worker threads, {minCompletionPortThreads} completion port threads");
        }

        var setResult = ThreadPool.SetMaxThreads(maxWorkerThreads, maxCompletionPortThreads);
        if (!setResult)
        {
            throw new Exception($"Failed to set max threads to {maxWorkerThreads} worker threads, {maxCompletionPortThreads} completion port threads");
        }

        // Print the final max threads setting
        ThreadPool.GetMinThreads(out minWorkerThreads, out minCompletionPortThreads);
        ThreadPool.GetMaxThreads(out maxWorkerThreads, out maxCompletionPortThreads);
        Console.WriteLine("");
        Console.WriteLine($"Final min threads: {minWorkerThreads} worker threads, {minCompletionPortThreads} completion port threads");
        Console.WriteLine($"Final max threads: {maxWorkerThreads} worker threads, {maxCompletionPortThreads} completion port threads");
        Console.WriteLine("");
    }

    public static void Main(string[] args)
    {
        AdjustThreadPool();
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
            .ConfigureServices(async (hostContext, services) =>
            {
                var config = Config.CreateAndValidate(hostContext.Configuration);
                services.AddSingleton<IConfig>(config);

                if (config.PreferredControlChannelScheme == "http")
                {
                    await GetCallbackIP.Init(port: config.ControlChannelInsecureHTTP2Port, scheme: "http").ConfigureAwait(false);
                }
                else
                {
                    await GetCallbackIP.Init(port: config.ControlChannelHTTP2Port, scheme: "https").ConfigureAwait(false);
                }
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
                // 5001 - incoming requests
                // 5002 - incoming requests HTTPS
                // 5003 - lambda interface HTTP
                // 5004 - lambda interface HTTPS
                webBuilder.ConfigureKestrel((context, serverOptions) =>
                {
                    // We have to reparse the config once, bummer
                    var config = Config.CreateAndValidate(context.Configuration);
                    //
                    // Incoming Requests
                    //
                    serverOptions.ListenAnyIP(config.IncomingRequestHTTPPort);
                    serverOptions.ListenAnyIP(config.IncomingRequestHTTPSPort, listenOptions =>
                    {
                        listenOptions.UseHttps(GetCertPath("lambdadispatch.local.pfx"));
                    });
                    //
                    // Control Channels
                    //
                    if (config.AllowInsecureControlChannel)
                    {
                        serverOptions.ListenAnyIP(config.ControlChannelInsecureHTTP2Port, o => o.Protocols = HttpProtocols.Http2);
                    }
                    serverOptions.ListenAnyIP(config.ControlChannelHTTP2Port, listenOptions =>
                    {
                        listenOptions.UseHttps(GetCertPath("lambdadispatch.local.pfx"));
                    });
                });
                webBuilder.UseStartup<Startup>();
            });
}