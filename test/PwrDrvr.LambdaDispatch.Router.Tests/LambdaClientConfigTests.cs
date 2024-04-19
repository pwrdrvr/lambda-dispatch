using Amazon.Lambda;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class LambdaClientConfigTests
{
  private string? _originalServiceUrl;

  [SetUp]
  public void SetUp()
  {
    // Save the original value of the environment variable
    _originalServiceUrl = Environment.GetEnvironmentVariable("AWS_LAMBDA_SERVICE_URL");

    // Clear the environment variable
    Environment.SetEnvironmentVariable("AWS_LAMBDA_SERVICE_URL", null);

    // Set the AWS_REGION environment variable
    Environment.SetEnvironmentVariable("AWS_REGION", "us-west-2");
  }

  [TearDown]
  public void TearDown()
  {
    // Restore the original value of the environment variable
    Environment.SetEnvironmentVariable("AWS_LAMBDA_SERVICE_URL", _originalServiceUrl);
  }

  [Test]
  public void LambdaClient_DefaultConfig_Success()
  {
    var lambdaClientConfig = new LambdaClientConfig();
    var lambdaClient = lambdaClientConfig.LambdaClient;

    Assert.That(lambdaClient, Is.Not.Null);
    Assert.That(lambdaClient, Is.InstanceOf<AmazonLambdaClient>());
    Assert.Multiple(() =>
    {
      Assert.That(lambdaClient.Config.Timeout, Is.EqualTo(TimeSpan.FromMinutes(15)));
      Assert.That(lambdaClient.Config.MaxErrorRetry, Is.EqualTo(1));
      Assert.That(lambdaClient.Config.ServiceURL, Is.Null);
    });
  }

  [Test]
  public void LambdaClient_ServiceUrlConfig_Success()
  {
    Environment.SetEnvironmentVariable("AWS_LAMBDA_SERVICE_URL", "http://localhost:5051");

    var lambdaClientConfig = new LambdaClientConfig();
    var lambdaClient = lambdaClientConfig.LambdaClient;

    Assert.That(lambdaClient, Is.Not.Null);
    Assert.That(lambdaClient, Is.InstanceOf<AmazonLambdaClient>());
    Assert.Multiple(() =>
    {
      Assert.That(lambdaClient.Config.Timeout, Is.EqualTo(TimeSpan.FromMinutes(15)));
      Assert.That(lambdaClient.Config.MaxErrorRetry, Is.EqualTo(1));
      Assert.That(lambdaClient.Config.ServiceURL, Is.EqualTo("http://localhost:5051/"));
    });
  }
}