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
  /// The number of channels to open back to the router from each lambda
  /// For very fast response times this should be a multiple (e.g. 2x or 4x) of
  /// the number of concurrent requests to send to a single Lambda.
  /// </summary>
  /// <default>MaxConcurrentCount * 2</default>
  int ChannelCount { get; }

  /// <summary>
  /// The HTTP (insecure) port the router listens on for requests that will be proxied to Lambda functions
  /// </summary>
  int IncomingRequestHTTPPort { get; }

  /// <summary>
  /// The HTTPS port the router listens on for requests that will be proxied to Lambda functions
  /// </summary>
  int IncomingRequestHTTPSPort { get; }

  /// <summary>
  /// The HTTP2 (secure, https) port the router listens on for Lambda control channel requests
  /// </summary>
  int ControlChannelHTTP2Port { get; }

  /// <summary>
  /// The HTTP2 (insecure, http) port the router listens on for Lambda control channel requests
  /// This port is only open if AllowInsecureControlChannel is set to true
  /// This port is only advertised as the default if PreferredControlChannelScheme is set to http
  /// </summary>
  public int ControlChannelInsecureHTTP2Port { get; set; }

  /// <summary>
  /// Should the insecure http2 control channel port be opened
  /// </summary>
  public bool AllowInsecureControlChannel { get; set; }

  /// <summary>
  /// The preferred scheme to use for the control channel (http or https)
  /// </summary>
  public string PreferredControlChannelScheme { get; set; }
}

public class Config : IConfig
{
  public string FunctionName { get; set; }

  public string FunctionNameOnly { get; private set; }

  public string? FunctionNameQualifier { get; private set; }

  public int MaxConcurrentCount { get; set; }

  public int ChannelCount { get; set; }

  public int IncomingRequestHTTPPort { get; set; }

  public int IncomingRequestHTTPSPort { get; set; }


  public int ControlChannelHTTP2Port { get; set; }

  public int ControlChannelInsecureHTTP2Port { get; set; }

  public bool AllowInsecureControlChannel { get; set; }

  public string PreferredControlChannelScheme { get; set; }

  public Config()
  {
    FunctionName = string.Empty;
    FunctionNameOnly = string.Empty;
    MaxConcurrentCount = 10;
    ChannelCount = -1;
    IncomingRequestHTTPPort = 5001;
    IncomingRequestHTTPSPort = 5002;
    ControlChannelInsecureHTTP2Port = 5003;
    ControlChannelHTTP2Port = 5004;
    AllowInsecureControlChannel = false;
    PreferredControlChannelScheme = "https";
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

    // Cannot prefer http when insecure is not allowed
    if (!AllowInsecureControlChannel && PreferredControlChannelScheme == "http")
    {
      throw new ApplicationException("Cannot prefer http for control channel when insecure is not allowed");
    }

    // Confirm scheme is http or https
    if (PreferredControlChannelScheme != "http" && PreferredControlChannelScheme != "https")
    {
      throw new ApplicationException($"Invalid PreferredControlChannelScheme in configuration: {PreferredControlChannelScheme}");
    }

    // Need at least as many channels as max concurrent
    if (ChannelCount == -1)
    {
      ChannelCount = Math.Min(MaxConcurrentCount * 2, 100);
    }
    else if (ChannelCount < MaxConcurrentCount)
    {
      throw new ApplicationException($"ChannelCount must be greater than or equal to MaxConcurrentCount");
    }
    else if (ChannelCount > 100)
    {
      throw new ApplicationException($"ChannelCount must be less than or equal to 100");
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