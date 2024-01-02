using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;
using Amazon.Lambda;
using Amazon.Lambda.Model;

namespace PwrDrvr.LambdaDispatch.Tests
{
  public class LambdaInstanceTests
  {
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void Constructor_ShouldRejectTooSmallMaxConcurrentCount()
    {
      Assert.Throws<ArgumentOutOfRangeException>(() => new LambdaInstance(0));
      Assert.Throws<ArgumentOutOfRangeException>(() => new LambdaInstance(-1));
    }

    [Test]
    public async Task AddConnection_ShouldAddAndCountConnections()
    {
      var maxConcurrentCount = 10;
      var lambdaClient = new Mock<IAmazonLambda>();
      var instance = new LambdaInstance(maxConcurrentCount, lambdaClient.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      // Start the Lambda
      await instance.Start();

      Assert.That(instance.AddConnection(request.Object, response.Object, "channel-1", false), Is.Not.Null);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(1));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(9));
      instance.AddConnection(request.Object, response.Object, "channel-2", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(8));
      instance.AddConnection(request.Object, response.Object, "channel-3", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(7));
      instance.AddConnection(request.Object, response.Object, "channel-4", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(6));
      instance.AddConnection(request.Object, response.Object, "channel-5", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(5));
      instance.AddConnection(request.Object, response.Object, "channel-6", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(4));
      instance.AddConnection(request.Object, response.Object, "channel-7", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(3));
      instance.AddConnection(request.Object, response.Object, "channel-8", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(2));
      instance.AddConnection(request.Object, response.Object, "channel-9", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(1));
      instance.AddConnection(request.Object, response.Object, "channel-10", false);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(10));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(10));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      // Can have more connections than maxConcurrentCount?
      // This kinda makes sense
      instance.AddConnection(request.Object, response.Object, "channel-11", false);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(11));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(10));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      // Get one connection
      Assert.That(instance.TryGetConnection(out var connection), Is.True);
      Assert.That(connection, Is.Not.Null);
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(10));
      // Even though we have 10 available connections, we're only supposed to use 9 more of them
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(9));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(1));

      // Get another connection
      Assert.That(instance.TryGetConnection(out connection), Is.True);
      Assert.That(connection, Is.Not.Null);
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(9));
      // Even though we have 9 available connections, we're only supposed to use 8 more of them
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(8));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(2));

      // Extract the remaining usable connections
      while (instance.TryGetConnection(out connection))
      {
        Assert.That(connection, Is.Not.Null);
      }
      // Check that we have 1 connection but that it is not usable
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(10));
    }

    [Test]
    public async Task AddConnection_ShouldRemoveAndReaddConnections()
    {
      var maxConcurrentCount = 10;
      var lambdaClient = new Mock<IAmazonLambda>();
      var instance = new LambdaInstance(maxConcurrentCount, lambdaClient.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      // Start the Lambda
      await instance.Start();

      instance.AddConnection(request.Object, response.Object, "channel-1", false);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(1));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(9));

      instance.AddConnection(request.Object, response.Object, "channel-2", false);
      instance.AddConnection(request.Object, response.Object, "channel-3", false);
      instance.AddConnection(request.Object, response.Object, "channel-4", false);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(6));

      Assert.That(instance.TryGetConnection(out var connection), Is.True);
      Assert.That(connection, Is.Not.Null);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(3));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(3));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(7));

      // Reinstate the connection
      instance.ReenqueueUnusedConnection(connection);
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(6));
    }
  }
}