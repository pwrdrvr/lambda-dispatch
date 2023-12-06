namespace PwrDrvr.LambdaDispatch.LambdaLB;

using Microsoft.Extensions.Logging;
using AWS.Logger;

public class LoggerInstance {
  private static ILoggerFactory _loggerFactory = LoggerFactory.Create(builder =>
    {
        builder
            .AddFilter(level => level >= Microsoft.Extensions.Logging.LogLevel.Debug)
            .AddConsole();
    });

  public static ILogger<T> CreateLogger<T>() {
    return _loggerFactory.CreateLogger<T>();
  }
}