namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class GetCallbackIPTests
{
  private IGetCallbackIP getCallbackIP;

  [Test]
  public void Constructor_WhenCalled_SetsPropertiesAndReturnsUrl()
  {
    getCallbackIP = new GetCallbackIP(5004, "https", "192.168.1.1");

    var url = getCallbackIP.CallbackUrl;

    Assert.That(url, Is.EqualTo("https://192.168.1.1:5004/api/chunked"));
  }

  [Test]
  public void Constructor_WhenNetworkIpIsNotSet_ThrowsException()
  {
    Assert.Throws<ArgumentException>(() => new GetCallbackIP(5004, "https", null));
  }
}