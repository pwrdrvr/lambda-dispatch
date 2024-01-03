namespace PwrDrvr.LambdaDispatch.Extension;

using Microsoft.Extensions.Logging;
using AWS.Logger;

public class LoggerInstance
{
  private static ILoggerFactory _loggerFactory = LoggerFactory.Create(builder =>
    {
      builder
#if DEBUG
          .AddFilter(level => level >= Microsoft.Extensions.Logging.LogLevel.Debug)
#else
            .AddFilter(level => level >= Microsoft.Extensions.Logging.LogLevel.Information)
#endif
          .AddConsole(options =>
          {
            options.IncludeScopes = true;
            options.TimestampFormat = "HH:mm:ss.fff ";
          });
    });

  public static ILogger<T> CreateLogger<T>()
  {
    return _loggerFactory.CreateLogger<T>();
  }
}