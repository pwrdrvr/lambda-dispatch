using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace PwrDrvr.LambdaDispatch.Router
{
    public class Startup
    {
        public IConfiguration Configuration { get; }

        public Startup()
        {
            var configuration = new ConfigurationBuilder()
                // .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables(prefix: "LAMBDA_DISPATCH_")
                .Build();

            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            var config = Config.CreateAndValidate(Configuration);
            services.AddSingleton<IConfig>(config);

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

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.MapWhen(context => context.Request.HttpContext.Connection.LocalPort == 5002, builder =>  // Public interface
            {
                builder.UseRouting();
                builder.UseEndpoints(endpoints =>
                {
                    endpoints.MapHealthChecks("/health");
                    endpoints.MapControllers();  // Map the IncomingController
                    endpoints.MapFallback(context =>
                    {
                        context.Response.StatusCode = 404;
                        return context.Response.WriteAsync("Unhandled route - Does not call LambdaLB\r\n");
                    });

                    // Add more routes for the public interface here
                });
            });

            app.MapWhen(context => context.Request.HttpContext.Connection.LocalPort == 5001 || context.Request.HttpContext.Connection.LocalPort == 5003, builder =>  // Control interface
            {
                builder.UseRouting();
                builder.UseEndpoints(endpoints =>
                {
                    endpoints.MapHealthChecks("/health");
                    endpoints.MapControllers();  // Map the ChunkedController
                    // endpoints.MapFallback(() => Console.WriteLine("Unhandled route"));
                    // Add more routes for the control interface here
                });
            });
        }
    }
}