using Amazon.Lambda;

namespace PwrDrvr.LambdaDispatch.Router;

public interface ILambdaClientConfig
{
  IAmazonLambda LambdaClient { get; }
}

public class LambdaClientConfig : ILambdaClientConfig
{
  public IAmazonLambda LambdaClient { get; }

  public LambdaClientConfig()
  {
    LambdaClient = new AmazonLambdaClient(CreateConfig());
  }

  private static AmazonLambdaConfig CreateConfig()
  {
    var serviceUrl = System.Environment.GetEnvironmentVariable("AWS_LAMBDA_SERVICE_URL");
    var config = new AmazonLambdaConfig
    {
      Timeout = TimeSpan.FromMinutes(15),
      MaxErrorRetry = 1,
    };

    if (!string.IsNullOrEmpty(serviceUrl))
    {
      config.ServiceURL = serviceUrl;
    }

    return config;
  }
}