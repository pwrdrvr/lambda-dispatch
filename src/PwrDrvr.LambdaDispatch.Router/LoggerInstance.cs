namespace PwrDrvr.LambdaDispatch.Router;

using Microsoft.Extensions.Logging;
using AWS.Logger;

public class LoggerInstance
{
  private static readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder =>
    {
      var awsOptions = new AWSLoggerConfig
      {
        Region = System.Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1",
        // Disable JSON formatting to keep plain text with newlines
        DisableLogGroupCreation = true,
      };

      builder
#if DEBUG
          .AddFilter(level => level >= Microsoft.Extensions.Logging.LogLevel.Debug);
#else
          .AddFilter(level => level >= Microsoft.Extensions.Logging.LogLevel.Information);
#endif

      if (Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI") == null)
      {
        builder.AddSimpleConsole(options =>
        {
          options.TimestampFormat = "HH:mm:ss.fff ";
        });
      }
      else
      {
        builder.AddAWSProvider(awsOptions);
      }
    });

  public static ILogger<T> CreateLogger<T>()
  {
    return _loggerFactory.CreateLogger<T>();
  }
}