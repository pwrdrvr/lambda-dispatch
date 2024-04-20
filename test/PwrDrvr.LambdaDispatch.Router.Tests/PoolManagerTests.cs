using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace PwrDrvr.LambdaDispatch.Router.Tests
{
  [TestFixture]
  public class PoolManagerTests
  {
    private Mock<IServiceProvider> _mockServiceProvider;
    private Mock<IConfig> _mockConfig;
    private PoolManager _poolManager;

    private Mock<IServiceScope> _mockServiceScope;
    private Mock<IServiceScopeFactory> _mockServiceScopeFactory;

    [SetUp]
    public void SetUp()
    {
      _mockServiceProvider = new Mock<IServiceProvider>();
      _mockConfig = new Mock<IConfig>();
      _mockConfig.SetupGet(c => c.FunctionName).Returns("default");

      _mockServiceScope = new Mock<IServiceScope>();
      _mockServiceScopeFactory = new Mock<IServiceScopeFactory>();

      _mockServiceScopeFactory.Setup(x => x.CreateScope()).Returns(_mockServiceScope.Object);
      _mockServiceProvider.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(_mockServiceScopeFactory.Object);
      _mockServiceScope.Setup(x => x.ServiceProvider.GetService(typeof(IPoolOptions))).Returns(new Mock<IPoolOptions>().Object);
      _mockServiceScope.Setup(x => x.ServiceProvider.GetService(typeof(IDispatcher))).Returns(new Mock<IDispatcher>().Object);

      _poolManager = new PoolManager(_mockServiceProvider.Object, _mockConfig.Object);
    }

    [Test]
    public void GetOrCreatePoolByLambdaName_ShouldReturnSameInstanceInSequence()
    {
      // Arrange
      var lambdaArn = "testLambdaArn";
      var results = new ConcurrentBag<IDispatcher>();
      var iterations = 100;
      var threads = new List<Thread>();

      // Act
      for (int i = 0; i < iterations; i++)
      {
        var result = _poolManager.GetOrCreatePoolByLambdaName(lambdaArn);
        results.Add(result.Dispatcher);
      }

      // Assert
      Assert.That(results.All(x => x == results.First()));
      _mockServiceScopeFactory.Verify(x => x.CreateScope(), Times.Once);
      _mockServiceProvider.Verify(x => x.GetService(typeof(IServiceScopeFactory)), Times.Once);
      _mockServiceScope.Verify(x => x.ServiceProvider.GetService(typeof(IPoolOptions)), Times.Once);
      _mockServiceScope.Verify(x => x.ServiceProvider.GetService(typeof(IDispatcher)), Times.Once);
    }

    [Test]
    public void GetOrCreatePoolByLambdaName_ShouldReturnSameInstanceInParallel()
    {
      // Arrange
      var lambdaArn = "default";
      var results = new ConcurrentBag<IDispatcher>();
      var exceptions = new ConcurrentBag<Exception>();
      var iterations = 100;
      var threads = new List<Thread>();

      // Act
      for (int i = 0; i < iterations; i++)
      {
        var thread = new Thread(() =>
        {
          try
          {
            var result = _poolManager.GetOrCreatePoolByLambdaName(lambdaArn);
            results.Add(result.Dispatcher);
          }
          catch (Exception ex)
          {
            exceptions.Add(ex);
          }
        });

        threads.Add(thread);
      }

      foreach (var thread in threads)
      {
        thread.Start();
      }

      foreach (var thread in threads)
      {
        thread.Join();
      }

      // Assert
      Assert.Multiple(() =>
      {
        Assert.That(exceptions, Is.Empty, $"Exceptions were thrown: {string.Join(", ", exceptions.Select(e => e.Message))}");
        Assert.That(results.All(x => x == results.First()));
      });
      _mockServiceScopeFactory.Verify(x => x.CreateScope(), Times.Once);
      _mockServiceProvider.Verify(x => x.GetService(typeof(IServiceScopeFactory)), Times.Once);
      _mockServiceScope.Verify(x => x.ServiceProvider.GetService(typeof(IPoolOptions)), Times.Once);
      _mockServiceScope.Verify(x => x.ServiceProvider.GetService(typeof(IDispatcher)), Times.Once);
    }

    [Test]
    public void GetOrCreatePoolByLambdaName_ShouldReturnCorrectValue()
    {
      // Arrange
      var lambdaArn = "testLambdaArn";

      // Act
      var result = _poolManager.GetOrCreatePoolByLambdaName(lambdaArn);

      // Assert
      Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void GetPoolByPoolId_ShouldReturnCorrectValue()
    {
      // Arrange
      var poolId = "testPoolId";

      // Act
      var result = _poolManager.GetPoolByPoolId(poolId, out var pool);

      // Assert
      Assert.Multiple(() =>
      {
        Assert.That(result, Is.False);
        Assert.That(pool, Is.Null);
      });
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