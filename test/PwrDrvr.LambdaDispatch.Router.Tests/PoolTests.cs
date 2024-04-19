using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;

namespace PwrDrvr.LambdaDispatch.Router.Tests
{
  [TestFixture]
  public class PoolTests
  {
    [Test]
    public void DispatcherProperty_ShouldReturnCorrectValue()
    {
      // Arrange
      var mockDispatcher = new Mock<IDispatcher>();
      var poolId = "testPoolId";
      var pool = new Pool(mockDispatcher.Object, poolId);

      // Act
      var result = pool.Dispatcher;

      // Assert
      Assert.That(result, Is.EqualTo(mockDispatcher.Object));
    }

    [Test]
    public void PoolIdProperty_ShouldReturnCorrectValue()
    {
      // Arrange
      var mockDispatcher = new Mock<IDispatcher>();
      var poolId = "testPoolId";
      var pool = new Pool(mockDispatcher.Object, poolId);

      // Act
      var result = pool.PoolId;

      // Assert
      Assert.That(result, Is.EqualTo(poolId));
    }
  }
}