using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace PwrDrvr.LambdaDispatch.Router;
public class Startup
{
    // public IConfiguration Configuration { get; }

    public Startup()
    {
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

        services.AddSingleton<ILambdaInstanceManager, LambdaInstanceManager>();
        services.AddSingleton<Dispatcher>();

        Task.Run(MetricsRegistry.PrintMetrics).ConfigureAwait(false);
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IConfig config)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

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
}

public class IncomingRequestMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Dispatcher _dispatcher;
    private readonly int[] _allowedPorts;

    public IncomingRequestMiddleware(RequestDelegate next, Dispatcher dispatcher, int[] allowedPorts)
    {
        _next = next;
        _dispatcher = dispatcher;
        _allowedPorts = allowedPorts;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_allowedPorts.Contains(context.Connection.LocalPort))
        {
            // Handle /health route
            if (context.Request.Path == "/health")
            {
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync("OK");
                return;
            }

            // We're going to handle this
            // We will prevent the endpoint router from ever seeing this request
            await _dispatcher.AddRequest(context.Request, context.Response);
        }
        else
        {
            await _next(context);
        }
    }
}

public class PortRestrictionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int[] _allowedPorts;

    public PortRestrictionMiddleware(RequestDelegate next, int[] allowedPorts)
    {
        _next = next;
        _allowedPorts = allowedPorts;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_allowedPorts.Contains(context.Connection.LocalPort))
        {
            await _next(context);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Not found");
        }
    }
}