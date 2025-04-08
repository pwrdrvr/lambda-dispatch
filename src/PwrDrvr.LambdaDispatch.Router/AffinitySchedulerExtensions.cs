using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PwrDrvr.LambdaDispatch.Router;

public static class AffinitySchedulerExtensions
{
  // Static field to hold the scheduler instance
  private static AffinityTaskScheduler _scheduler;
  private static TaskFactory _taskFactory;
  private static AffinitySynchronizationContext _synchronizationContext;

  /// <summary>
  /// Configures ASP.NET Core to use the AffinityTaskScheduler for all tasks
  /// </summary>
  public static IHostBuilder UseAffinityTaskScheduler(this IHostBuilder builder, int? threadCount = null)
  {
    return builder.ConfigureServices((hostContext, services) =>
    {
      // Initialize the scheduler with specified thread count or processor count
      _scheduler = new AffinityTaskScheduler(threadCount ?? Environment.ProcessorCount);

      // Create a TaskFactory that uses our scheduler
      _taskFactory = new TaskFactory(
              CancellationToken.None,
              TaskCreationOptions.None,
              TaskContinuationOptions.None,
              _scheduler);

      // Create the synchronization context
      _synchronizationContext = new AffinitySynchronizationContext(_taskFactory);

      // Register services for DI
      services.AddSingleton(_scheduler);
      services.AddSingleton(_taskFactory);
      services.AddSingleton(_synchronizationContext);

      // Register a startup filter that will apply our SynchronizationContext
      services.AddSingleton<IStartupFilter, AffinityStartupFilter>();
    });
  }

  /// <summary>
  /// Applies the AffinityTaskScheduler to an existing WebApplicationBuilder
  /// </summary>
  public static WebApplicationBuilder UseAffinityTaskScheduler(this WebApplicationBuilder builder, int? threadCount = null)
  {
    // Initialize the scheduler with specified thread count or processor count
    _scheduler = new AffinityTaskScheduler(threadCount ?? Environment.ProcessorCount);

    // Create a TaskFactory that uses our scheduler
    _taskFactory = new TaskFactory(
        CancellationToken.None,
        TaskCreationOptions.None,
        TaskContinuationOptions.None,
        _scheduler);

    // Create the synchronization context
    _synchronizationContext = new AffinitySynchronizationContext(_taskFactory);

    // Register services for DI
    builder.Services.AddSingleton(_scheduler);
    builder.Services.AddSingleton(_taskFactory);
    builder.Services.AddSingleton(_synchronizationContext);

    // Register a startup filter that will apply our SynchronizationContext
    builder.Services.AddSingleton<IStartupFilter, AffinityStartupFilter>();

    return builder;
  }

  // Helper method to cleanly shut down the scheduler
  public static void ShutdownAffinityScheduler()
  {
    _scheduler?.Dispose();
  }

  /// <summary>
  /// A filter that applies our SynchronizationContext early in the pipeline
  /// </summary>
  private class AffinityStartupFilter : IStartupFilter
  {
    private readonly AffinitySynchronizationContext _synchronizationContext;

    public AffinityStartupFilter(AffinitySynchronizationContext synchronizationContext)
    {
      _synchronizationContext = synchronizationContext;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
      return app =>
      {
        // Add middleware that ensures our SynchronizationContext is applied
        app.Use(async (context, nextMiddleware) =>
              {
                // Save the original SynchronizationContext
                var originalContext = SynchronizationContext.Current;

                try
                {
                  // Set our custom SynchronizationContext
                  SynchronizationContext.SetSynchronizationContext(_synchronizationContext);

                  // Continue with the next middleware
                  await nextMiddleware();
                }
                finally
                {
                  // Restore the original SynchronizationContext
                  SynchronizationContext.SetSynchronizationContext(originalContext);
                }
              });

        // Call the next configure action
        next(app);
      };
    }
  }

  /// <summary>
  /// Custom SynchronizationContext that ensures all posted callbacks use our scheduler
  /// </summary>
  public class AffinitySynchronizationContext : SynchronizationContext
  {
    private readonly TaskFactory _taskFactory;

    public AffinitySynchronizationContext(TaskFactory taskFactory)
    {
      _taskFactory = taskFactory;
    }

    public override void Post(SendOrPostCallback d, object state)
    {
      // Schedule the callback using our TaskFactory
      _taskFactory.StartNew(() => d(state), CancellationToken.None,
          TaskCreationOptions.None, _taskFactory.Scheduler);
    }

    public override void Send(SendOrPostCallback d, object state)
    {
      // Execute the callback synchronously using our TaskFactory
      _taskFactory.StartNew(() => d(state), CancellationToken.None,
          TaskCreationOptions.None, _taskFactory.Scheduler).GetAwaiter().GetResult();
    }
  }
}

// Example of integration in Program.cs
// public class Program
// {
//   public static void Main(string[] args)
//   {
//     try
//     {
//       // For .NET 6+ minimal hosting model
//       var builder = WebApplication.CreateBuilder(args);

//       // Apply our custom scheduler
//       builder.UseAffinityTaskScheduler();

//       // Add services to the container
//       builder.Services.AddControllers();

//       var app = builder.Build();

//       // Configure the HTTP request pipeline
//       app.UseRouting();
//       app.UseEndpoints(endpoints =>
//       {
//         endpoints.MapControllers();
//       });

//       app.Run();
//     }
//     finally
//     {
//       // Ensure we clean up the scheduler on shutdown
//       AffinitySchedulerExtensions.ShutdownAffinityScheduler();
//     }
//   }
// }