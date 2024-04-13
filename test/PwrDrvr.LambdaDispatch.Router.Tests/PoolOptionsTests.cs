using PwrDrvr.LambdaDispatch.Router;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

[TestFixture]
public class PoolOptionsTests
{
  [Test]
  public void Setup_WhenCalled_SetsProperties()
  {
    var poolOptions = new PoolOptions();

    poolOptions.Setup("lambdaName", "poolId");

    Assert.That(poolOptions.LambdaName, Is.EqualTo("lambdaName"));
    Assert.That(poolOptions.PoolId, Is.EqualTo("poolId"));
  }

  [Test]
  public void Setup_WhenCalledTwice_ThrowsException()
  {
    var poolOptions = new PoolOptions();
    poolOptions.Setup("lambdaName", "poolId");

    Assert.Throws<InvalidOperationException>(() => poolOptions.Setup("anotherLambdaName", "anotherPoolId"));
  }
}