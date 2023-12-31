using Amazon.Lambda;
using Amazon.Lambda.Model;

namespace PwrDrvr.LambdaDispatch.Router;

public static class LambdaClientConfig
{
  private static AmazonLambdaConfig CreateConfig()
  {
    // Set env var AWS_LAMBDA_SERVICE_URL=http://host.docker.internal:5051
    // When testing with LambdaTestTool hosted outside of the dev container
    var serviceUrl = System.Environment.GetEnvironmentVariable("AWS_LAMBDA_SERVICE_URL");
    var config = new AmazonLambdaConfig
    {
      Timeout = TimeSpan.FromMinutes(15),
      MaxErrorRetry = 8
    };

    if (!string.IsNullOrEmpty(serviceUrl))
    {
      config.ServiceURL = serviceUrl;
    }

    return config;
  }

  public static readonly IAmazonLambda LambdaClient = new AmazonLambdaClient(CreateConfig());
}