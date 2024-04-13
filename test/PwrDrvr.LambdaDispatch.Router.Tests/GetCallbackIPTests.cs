namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class GetCallbackIPTests
{
  [SetUp]
  public void Setup()
  {
    Environment.SetEnvironmentVariable("ROUTER_CALLBACK_HOST", null);
  }

  [Test]
  public void Init_WhenCalled_SetsPropertiesAndReturnsUrl()
  {
    Environment.SetEnvironmentVariable("ROUTER_CALLBACK_HOST", null);

    var url = GetCallbackIP.Init(5004, "https", "192.168.1.1");

    Assert.That(url, Is.EqualTo("https://192.168.1.1:5004/api/chunked"));
  }

  [Test]
  public void Get_WhenNetworkIpIsNotSet_ThrowsException()
  {
    Assert.Throws<Exception>(() => GetCallbackIP.Get());
  }

  // Can run this when class is made non-static
  // [Test]
  // public void Get_WhenRouterCallbackHostIsSet_ReturnsUrlWithHost()
  // {
  //   Environment.SetEnvironmentVariable("ROUTER_CALLBACK_HOST", "myhost");
  //   GetCallbackIP.Init(5004, "https", "192.168.1.1");

  //   var url = GetCallbackIP.Get();

  //   Assert.That(url, Is.EqualTo("https://myhost:5004/api/chunked"));
  // }
}