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
}

public class Config : IConfig
{
  public string FunctionName { get; set; }

  public string FunctionNameOnly { get; private set; }

  public string? FunctionNameQualifier { get; private set; }

  public int MaxConcurrentCount { get; set; }

  public Config()
  {
    FunctionName = string.Empty;
    MaxConcurrentCount = 10;
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
  }

  private (string, string?) ParseFunctionName(string functionName)
  {
    var parts = functionName.Split(':');
    if (parts.Length > 1)
    {
      var qualifier = parts.Last();
      var baseFunctionName = string.Join(':', parts.Take(parts.Length - 1));
      return (baseFunctionName, qualifier);
    }
    else
    {
      return (parts[0], null);
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