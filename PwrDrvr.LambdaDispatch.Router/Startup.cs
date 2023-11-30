using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PwrDrvr.LambdaDispatch.Router
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();
            services.AddHealthChecks();
            services.AddControllers();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.MapWhen(context => context.Request.Host.Port == 5000, builder =>  // Public interface
            {
                builder.UseRouting();
                builder.UseEndpoints(endpoints =>
                {
                    endpoints.MapHealthChecks("/health");
                    endpoints.MapFallback(() => "Hello World!");

                    // Add more routes for the public interface here
                });
            });

            app.MapWhen(context => context.Request.Host.Port == 5001, builder =>  // Control interface
            {
                builder.UseRouting();
                builder.UseEndpoints(endpoints =>
                {
                    endpoints.MapHealthChecks("/health");
                    endpoints.MapControllers();  // Map the ChunkedController
                    // endpoints.MapFallback(() => "Control Interface");
                    // Add more routes for the control interface here
                });
            });
        }
    }
}