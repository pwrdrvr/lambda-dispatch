using Microsoft.AspNetCore.Server.Kestrel.Core;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

namespace PwrDrvr.LambdaDispatch.Router;

public class Program
{
    private static readonly ILogger _logger = LoggerInstance.CreateLogger<Program>();

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

        _logger.LogInformation("Default min threads: {minWorkerThreads} worker threads, {minCompletionPortThreads} completion port threads", minWorkerThreads, minCompletionPortThreads);
        _logger.LogInformation("Default max threads: {maxWorkerThreads} worker threads, {maxCompletionPortThreads} completion port threads", maxWorkerThreads, maxCompletionPortThreads);

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
        _logger.LogInformation("Final min threads: {minWorkerThreads} worker threads, {minCompletionPortThreads} completion port threads", minWorkerThreads, minCompletionPortThreads);
        _logger.LogInformation("Final max threads: {maxWorkerThreads} worker threads, {maxCompletionPortThreads} completion port threads", maxWorkerThreads, maxCompletionPortThreads);
    }

    public static void Main(string[] args)
    {
        _logger.LogInformation("GIT_HASH: {GIT_HASH}", Environment.GetEnvironmentVariable("GIT_HASH") ?? "none");
        _logger.LogInformation("BUILD_TIME: {BUILD_TIME}", Environment.GetEnvironmentVariable("BUILD_TIME") ?? "none");
        AdjustThreadPool();
        CreateHostBuilder(args).Build().Run();
    }

    public static string? GetCertPath(string filename)
    {
        // Check if the 'certs/' folder is in the current directory
        var currentDirectory = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(currentDirectory, "certs"))
            && File.Exists(Path.Combine(currentDirectory, "certs", filename)))
        {
            return Path.Combine(currentDirectory, "certs", filename);
        }

        // Check if the 'certs/' folder is two directories up
        var parent1 = Directory.GetParent(currentDirectory);
        if (parent1 == null)
        {
            return null;
        }
        var parent2 = Directory.GetParent(parent1.FullName);
        if (parent2 == null)
        {
            return null;
        }
        var twoDirectoriesUp = parent2.FullName;
        if (Directory.Exists(Path.Combine(twoDirectoriesUp, "certs"))
            && File.Exists(Path.Combine(twoDirectoriesUp, "certs", filename)))
        {
            return Path.Combine(twoDirectoriesUp, "certs", filename);
        }

        return null;
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

                var metadataService = new MetadataService(config: config);
                _logger.LogInformation("CALLBACK NETWORK IP/HOST: {NetworkIP}", metadataService.NetworkIP);
                services.AddSingleton<IMetadataService>(metadataService);

                var metricsDimensions = new Dictionary<string, string>
                {
                    ["Service"] = "LambdaDispatch.Router"
                };
                if (metadataService.ClusterName != null)
                {
                    metricsDimensions["ClusterName"] = metadataService.ClusterName;
                }
                else
                {
                    metricsDimensions["ClusterName"] = "Local";
                }

                IMetricsLogger metrics = config.CloudWatchMetricsEnabled
                    ? metrics = new MetricsLogger("PwrDrvr", metricsDimensions)
                    : metrics = new MetricsLoggerDummy();
                services.AddSingleton(metrics);
                metrics.PutMetric("Startup", 1, Unit.Count);

                if (config.PreferredControlChannelScheme == "http")
                {
                    services.AddSingleton<IGetCallbackIP>(new GetCallbackIP(port: config.ControlChannelInsecureHTTP2Port, scheme: "http", networkIp: metadataService.NetworkIP));
                }
                else
                {
                    services.AddSingleton<IGetCallbackIP>(new GetCallbackIP(port: config.ControlChannelHTTP2Port, scheme: "https", networkIp: metadataService.NetworkIP));
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
                    // Turn off body size limits
                    serverOptions.Limits.MaxRequestBodySize = null;

                    // Remove the `Server: Kestrel` response header
                    serverOptions.AddServerHeader = false;

                    // See if we have a cert
                    var certPath = GetCertPath("lambdadispatch.local.pfx");

                    // We have to reparse the config once, bummer
                    var config = Config.CreateAndValidate(context.Configuration);
                    _logger.LogInformation("{Config}", config);

                    //
                    // Incoming Requests
                    //
                    serverOptions.ListenAnyIP(config.IncomingRequestHTTPPort);
                    if (certPath == null)
                    {
                        _logger.LogWarning("No cert found, not starting HTTPS incoming request channel");
                    }
                    else
                    {
                        serverOptions.ListenAnyIP(config.IncomingRequestHTTPSPort, listenOptions =>
                        {
                            listenOptions.UseHttps(certPath);
                        });
                    }
                    //
                    // Control Channels
                    //
                    if (config.AllowInsecureControlChannel)
                    {
                        serverOptions.ListenAnyIP(config.ControlChannelInsecureHTTP2Port, o => o.Protocols = HttpProtocols.Http2);
                    }
                    if (certPath == null)
                    {
                        _logger.LogWarning("No cert found, not starting HTTPS control channel");
                    }
                    else
                    {
                        serverOptions.ListenAnyIP(config.ControlChannelHTTP2Port, listenOptions =>
                        {
                            listenOptions.UseHttps(certPath);
                        });
                    }
                });
                webBuilder.UseStartup(serviceProvider =>
                {
                    var config = Config.CreateAndValidate(serviceProvider.Configuration);
                    return new Startup(config);
                });
            });
}