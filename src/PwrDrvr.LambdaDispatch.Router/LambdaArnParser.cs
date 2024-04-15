using System.Text.RegularExpressions;

namespace PwrDrvr.LambdaDispatch.Router;

public static class LambdaArnParser
{
  public static (string functionName, string? qualifier) ParseFunctionName(string functionName)
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

  public static bool IsValidLambdaArgument(string functionName)
  {
    if (string.IsNullOrWhiteSpace(functionName)
       || (!IsValidLambdaName(functionName) &&
           !IsValidLambdaNameWithQualifier(functionName) &&
           !IsValidLambdaArn(functionName)))
    {
      return false;
    }

    return true;
  }

  public static bool IsValidLambdaName(string functionName)
  {
    var regex = new Regex(@"^[a-zA-Z0-9-_]{1,64}$");
    return regex.IsMatch(functionName);
  }

  public static bool IsValidLambdaNameWithQualifier(string functionName)
  {
    var regex = new Regex(@"^[a-zA-Z0-9-_]{1,64}:([a-zA-Z0-9-_]{1,128}|\$LATEST)$");
    return regex.IsMatch(functionName);
  }

  public static bool IsValidLambdaArn(string functionName)
  {
    var regex = new Regex(@"^arn:aws:lambda:[a-z0-9-]+:[0-9]{12}:function:[a-zA-Z0-9-_]{1,64}(:[a-zA-Z0-9-_]{1,128}|:\$LATEST)?$");
    return regex.IsMatch(functionName);
  }
}