namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class GetCallbackIPTests
{
  [Test]
  public void Constructor_WhenCalled_SetsPropertiesAndReturnsUrl()
  {
    var getCallbackIP = new GetCallbackIP(5004, "https", "192.168.1.1");

    var url = getCallbackIP.CallbackUrl;

    Assert.That(url, Is.EqualTo("https://192.168.1.1:5004/api/chunked"));
  }

  [Test]
  public void Constructor_WhenNetworkIpIsNotSet_ThrowsException()
  {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
    Assert.Throws<ArgumentException>(() => new GetCallbackIP(5004, "https", null));
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
  }
}