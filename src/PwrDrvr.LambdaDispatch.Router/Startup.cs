using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace PwrDrvr.LambdaDispatch.Router;

public class Startup
{
    private readonly ILogger _logger = LoggerInstance.CreateLogger<Startup>();
    private readonly ShutdownSignal _shutdownSignal = new();

    private readonly IConfig _config;

    public Startup(IConfig config)
    {
        _config = config;
        // var configuration = new ConfigurationBuilder()
        //     // .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        //     .AddEnvironmentVariables(prefix: "LAMBDA_DISPATCH_")
        //     .Build();

        // Configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.Configure<KestrelServerOptions>(options =>
            {
                options.Limits.MinRequestBodyDataRate = null;
                options.Limits.MinResponseDataRate = null;
            });

        services.AddRouting();
        services.AddHealthChecks();
        services.AddControllers();

        // services.AddSingleton<ILambdaInstanceQueue, RoundRobinLambdaInstanceQueue>();
        // services.AddSingleton<ILambdaInstanceQueue, RoundRobinLambdaInstanceQueue2>();
#if !SKIP_METRICS
        var metricsRegistry = new MetricsRegistry();
        services.AddSingleton<IMetricsRegistry>(metricsRegistry);
#endif
        services.AddSingleton<ILambdaClientConfig, LambdaClientConfig>();
        services.AddScoped<IPoolOptions, PoolOptions>();
        services.AddScoped<ILambdaInstanceQueue, LeastOutstandingQueue>();
        services.AddScoped<ILambdaInstanceManager, LambdaInstanceManager>();
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddSingleton<IPoolManager, PoolManager>();
        services.AddSingleton<IShutdownSignal>(_shutdownSignal);

#if !SKIP_METRICS
        if (_config.LogPeriodicMetrics)
        {
            Task.Run(metricsRegistry.PrintMetrics).ConfigureAwait(false);
        }
#endif
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IConfig config, IHostApplicationLifetime applicationLifetime)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        applicationLifetime.ApplicationStopping.Register(OnShutdown);

        // Handle the proxied requests with middleware so we do not
        // have two competing sets of controllers
        // TODO: Evaluate the performance of using Middleware without Controllers for the control channel
        app.UseMiddleware<IncomingRequestMiddleware>(new[] { config.IncomingRequestHTTPPort, config.IncomingRequestHTTPSPort });
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            // This will only be on control channel port
            // All incoming proxied requests were stolen by the IncomingRequestMiddleware above
            endpoints.MapAreaControllerRoute(
                    name: "default",
                    areaName: "ControlChannels",
                    pattern: "{*url}");
        });
    }

    private void OnShutdown()
    {
        _logger.LogInformation("OnShutdown called");
        _shutdownSignal.Shutdown.Cancel();
    }
}
