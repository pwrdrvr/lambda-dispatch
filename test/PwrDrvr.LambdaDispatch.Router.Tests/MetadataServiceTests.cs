using Moq;
using System.Net;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class MetadataServiceTests
{
  private Mock<IHttpClientFactory> _httpClientFactoryMock;
  private HttpClient _client;

  private IConfig configWithRouterCallbackHost;
  private IConfig configWithoutRouterCallbackHost;

  [SetUp]
  public void SetUp()
  {
    Environment.SetEnvironmentVariable("AWS_EXECUTION_ENV", null);
    _httpClientFactoryMock = new Mock<IHttpClientFactory>();
    _client = new HttpClient(new MockHttpMessageHandler());
    _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(_client);
    configWithRouterCallbackHost = new Config { RouterCallbackHost = "192.168.1.1" };
    configWithoutRouterCallbackHost = new Config { RouterCallbackHost = null };
  }

  [TearDown]
  public void TearDown()
  {
    _client.Dispose();
  }

  [Test]
  public void Constructor_WhenConfigWithRouterCallbackHostIsPassed_SetsNetworkIP()
  {
    var metadataService = new MetadataService(config: configWithRouterCallbackHost);

    Assert.That(metadataService.NetworkIP, Is.EqualTo("192.168.1.1"));
  }

  [Test]
  public void Constructor_WhenConfigWithoutRouterCallbackHostIsPassed_SetsNetworkIPToDefault()
  {
    var metadataService = new MetadataService(config: configWithoutRouterCallbackHost);

    Assert.That(metadataService.NetworkIP, Is.EqualTo("127.0.0.1")); // Assuming that the default IP is "127.0.0.1"
  }

  [Test]
  public void Test_Local_ExecEnvType()
  {
    var service = new MetadataService(_httpClientFactoryMock.Object, configWithoutRouterCallbackHost);
    Assert.Multiple(() =>
   {
     Assert.That(service.NetworkIP, Is.EqualTo("127.0.0.1"));
     Assert.That(service.ClusterName, Is.Null);
   });
  }

  [Test]
  public void Test_ECS_ExecEnvType()
  {
    Environment.SetEnvironmentVariable("AWS_EXECUTION_ENV", "AWS_ECS_FARGATE");
    Environment.SetEnvironmentVariable("ECS_CONTAINER_METADATA_URI_V4", "http://localhost:1000/v4/metadata");
    var service = new MetadataService(_httpClientFactoryMock.Object, configWithoutRouterCallbackHost);
    Assert.Multiple(() =>
    {
      Assert.That(service.NetworkIP, Is.EqualTo("192.168.0.1"));
      Assert.That(service.ClusterName, Is.EqualTo("test_cluster"));
    });
  }
}

public class MockHttpMessageHandler : HttpMessageHandler
{
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    var responseMessage = new HttpResponseMessage(HttpStatusCode.OK);

    // Set the content of the response message based on the request URL
    if (request.RequestUri.ToString() == Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI_V4"))
    {
      responseMessage.Content = new StringContent("{ \"Networks\": [ { \"NetworkMode\": \"awsvpc\", \"IPv4Addresses\": [ \"192.168.0.1\" ] } ] }");
    }
    if (request.RequestUri.ToString() == $"{Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI_V4")}/task")
    {
      responseMessage.Content = new StringContent("{ \"TaskARN\": \"arn:aws:ecs:us-west-2:123456789012:task/test_cluster/abcdefg\" }");
    }

    return await Task.FromResult(responseMessage);
  }
}