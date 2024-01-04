using System.Text.RegularExpressions;

namespace PwrDrvr.LambdaDispatch.Router;

public interface IConfig
{
  /// <summary>
  /// Name, Name:Qualifier, or ARN of the Lambda function to invoke
  /// </summary>
  string FunctionName { get; }

  /// <summary>
  /// Only the name or name ARN of the Lambda function to invoke (minus any qualifier)
  /// </summary>
  string FunctionNameOnly { get; }

  /// <summary>
  /// Only the qualifier of the Lambda function to invoke
  /// </summary>
  string? FunctionNameQualifier { get; }

  /// <summary>
  /// Maximum number of concurrent requests to send to a single Lambda
  /// Drives the number of Lambda instances to create: `(ConcurrentRequests / MaxConcurrentCount) * 2`
  /// Drives the number of requests the Lambda instances send back to the router to pickup requests
  /// </summary>
  int MaxConcurrentCount { get; }

  /// <summary>
  /// The HTTP (insecure) port the router listens on for requests that will be proxied to Lambda functions
  /// </summary>
  int IncomingRequestHTTPPort { get; }

  /// <summary>
  /// The HTTPS port the router listens on for requests that will be proxied to Lambda functions
  /// </summary>
  int IncomingRequestHTTPSPort { get; }

  /// <summary>
  /// The HTTP2 (secure) port the router listens on for Lambda control channel requests
  /// </summary>
  int ControlChannelHTTP2Port { get; }
}

public class Config : IConfig
{
  public string FunctionName { get; set; }

  public string FunctionNameOnly { get; private set; }

  public string? FunctionNameQualifier { get; private set; }

  public int MaxConcurrentCount { get; set; }

  public int IncomingRequestHTTPPort { get; set; }

  public int IncomingRequestHTTPSPort { get; set; }

  public int ControlChannelHTTP2Port { get; set; }

  public Config()
  {
    FunctionName = string.Empty;
    MaxConcurrentCount = 10;
    IncomingRequestHTTPPort = 5002;
    IncomingRequestHTTPSPort = 5004;
    ControlChannelHTTP2Port = 5003;
  }

  public static Config CreateAndValidate(IConfiguration configuration)
  {
    var config = new Config();
    configuration.Bind(config);
    config.Validate();
    (config.FunctionNameOnly, config.FunctionNameQualifier) = config.ParseFunctionName(config.FunctionName);
    return config;
  }

  private void Validate()
  {
    if (string.IsNullOrWhiteSpace(FunctionName)
        || (!IsValidLambdaName(FunctionName) &&
            !IsValidLambdaNameWithQualifier(FunctionName) &&
            !IsValidLambdaArn(FunctionName)))
    {
      throw new ApplicationException($"Invalid FunctionName in configuration: {FunctionName}");
    }
    // Validate the ports
    if (IncomingRequestHTTPPort < 1 || IncomingRequestHTTPPort > 65535)
    {
      throw new ApplicationException($"Invalid IncomingRequestHTTPPort in configuration: {IncomingRequestHTTPPort}");
    }
    if (IncomingRequestHTTPSPort < 1 || IncomingRequestHTTPSPort > 65535)
    {
      throw new ApplicationException($"Invalid IncomingRequestHTTPSPort in configuration: {IncomingRequestHTTPSPort}");
    }
    if (ControlChannelHTTP2Port < 1 || ControlChannelHTTP2Port > 65535)
    {
      throw new ApplicationException($"Invalid ControlChannelHTTP2Port in configuration: {ControlChannelHTTP2Port}");
    }
  }

  private (string, string?) ParseFunctionName(string functionName)
  {
    var parts = functionName.Split(':');
    if (parts.Length == 2 || parts.Length == 8)
    {
      var qualifier = parts.Last();
      var baseFunctionName = string.Join(':', parts.Take(parts.Length - 1));
      return (baseFunctionName, qualifier);
    }
    else if (parts.Length == 7)
    {
      return (functionName, null);
    }
    else if (parts.Length == 1)
    {
      return (functionName, null);
    }
    else
    {
      throw new ApplicationException($"Invalid FunctionName in configuration: {functionName}");
    }
  }

  private bool IsValidLambdaName(string functionName)
  {
    var regex = new Regex(@"^[a-zA-Z0-9-_]{1,64}$");
    return regex.IsMatch(functionName);
  }

  private bool IsValidLambdaNameWithQualifier(string functionName)
  {
    var regex = new Regex(@"^[a-zA-Z0-9-_]{1,64}:([a-zA-Z0-9-_]{1,128}|\$LATEST)$");
    return regex.IsMatch(functionName);
  }

  private bool IsValidLambdaArn(string functionName)
  {
    var regex = new Regex(@"^arn:aws:lambda:[a-z0-9-]+:[0-9]{12}:function:[a-zA-Z0-9-_]{1,64}(:[a-zA-Z0-9-_]{1,128}|:\$LATEST)?$");
    return regex.IsMatch(functionName);
  }
}