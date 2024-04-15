using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;
using System;
using Microsoft.Extensions.DependencyInjection;

namespace PwrDrvr.LambdaDispatch.Router.Tests
{
  [TestFixture]
  public class PoolManagerTests
  {
    private Mock<IServiceProvider> _mockServiceProvider;
    private Mock<IConfig> _mockConfig;
    private PoolManager _poolManager;

    [SetUp]
    public void SetUp()
    {
      _mockServiceProvider = new Mock<IServiceProvider>();
      _mockConfig = new Mock<IConfig>();

      var mockServiceScope = new Mock<IServiceScope>();
      var mockServiceScopeFactory = new Mock<IServiceScopeFactory>();
      var mockPoolOptions = new Mock<IPoolOptions>();
      var mockDispatcher = new Mock<IDispatcher>();

      mockServiceScopeFactory.Setup(x => x.CreateScope()).Returns(mockServiceScope.Object);
      _mockServiceProvider.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(mockServiceScopeFactory.Object);
      mockServiceScope.Setup(x => x.ServiceProvider.GetService(typeof(IPoolOptions))).Returns(mockPoolOptions.Object);
      mockServiceScope.Setup(x => x.ServiceProvider.GetService(typeof(IDispatcher))).Returns(mockDispatcher.Object);

      _poolManager = new PoolManager(_mockServiceProvider.Object, _mockConfig.Object);
    }

    [Test]
    public void GetOrCreatePoolByLambdaName_ShouldReturnCorrectValue()
    {
      // Arrange
      var lambdaArn = "testLambdaArn";

      // Act
      var result = _poolManager.GetOrCreatePoolByLambdaName(lambdaArn);

      // Assert
      Assert.IsNotNull(result);
    }

    [Test]
    public void GetPoolByPoolId_ShouldReturnCorrectValue()
    {
      // Arrange
      var poolId = "testPoolId";

      // Act
      var result = _poolManager.GetPoolByPoolId(poolId, out var pool);

      // Assert
      Assert.IsFalse(result);
      Assert.IsNull(pool);
    }

    [Test]
    public void RemovePoolByArn_ShouldNotThrowException()
    {
      // Arrange
      var lambdaArn = "testLambdaArn";

      // Act
      TestDelegate action = () => _poolManager.RemovePoolByArn(lambdaArn);

      // Assert
      Assert.DoesNotThrow(action);
    }
  }
}