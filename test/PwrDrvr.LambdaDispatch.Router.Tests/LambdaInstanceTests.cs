using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;
using Amazon.Lambda;
using Amazon.Lambda.Model;

namespace PwrDrvr.LambdaDispatch.Router.Tests
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
      Assert.Throws<ArgumentOutOfRangeException>(() => new LambdaInstance(0, "somefunc"));
      Assert.Throws<ArgumentOutOfRangeException>(() => new LambdaInstance(-1, "somefunc"));
    }

    [Test]
    public async Task AddConnection_ShouldAddAndCountConnections()
    {
      var maxConcurrentCount = 10;
      var lambdaClient = new Mock<IAmazonLambda>();
      var instance = new LambdaInstance(maxConcurrentCount, "somefunc", null, lambdaClient.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      // Start the Lambda
      await instance.Start();

      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));

      Assert.That(instance.AddConnection(request.Object, response.Object, "channel-1", false), Is.Not.Null);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(1));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      await instance.AddConnection(request.Object, response.Object, "channel-2", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(2));
      await instance.AddConnection(request.Object, response.Object, "channel-3", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(3));
      await instance.AddConnection(request.Object, response.Object, "channel-4", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      await instance.AddConnection(request.Object, response.Object, "channel-5", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(5));
      await instance.AddConnection(request.Object, response.Object, "channel-6", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(6));
      await instance.AddConnection(request.Object, response.Object, "channel-7", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(7));
      await instance.AddConnection(request.Object, response.Object, "channel-8", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(8));
      await instance.AddConnection(request.Object, response.Object, "channel-9", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(9));
      await instance.AddConnection(request.Object, response.Object, "channel-10", false);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(10));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(10));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      // Can have more connections than maxConcurrentCount?
      // This kinda makes sense
      await instance.AddConnection(request.Object, response.Object, "channel-11", false);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(11));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(10));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
    }

    [Test]
    public async Task AddConnection_ShouldHideSurplusConnections()
    {
      var maxConcurrentCount = 10;
      var lambdaClient = new Mock<IAmazonLambda>();
      var instance = new LambdaInstance(maxConcurrentCount, "somefunc", null, lambdaClient.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      // Start the Lambda
      await instance.Start();

      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));

      // These should be visible
      for (var i = 1; i <= maxConcurrentCount; i++)
      {
        Assert.That(instance.AddConnection(request.Object, response.Object, $"channel-{i}", false), Is.Not.Null);
        Assert.That(instance.QueueApproximateCount, Is.EqualTo(i));
        Assert.That(instance.AvailableConnectionCount, Is.EqualTo(i));
        Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      }

      // These should be hidden
      for (var i = 1; i <= maxConcurrentCount; i++)
      {
        Assert.That(instance.AddConnection(request.Object, response.Object, $"channel-{i + 10}", false), Is.Not.Null);
        Assert.That(instance.QueueApproximateCount, Is.EqualTo(i + maxConcurrentCount));
        Assert.That(instance.AvailableConnectionCount, Is.EqualTo(maxConcurrentCount));
        Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      }

      // Should give back only the non-hidden connections
      for (var i = 1; i <= maxConcurrentCount; i++)
      {
        Assert.That(instance.TryGetConnection(out var connection), Is.True);
        Assert.That(connection, Is.Not.Null);
        Assert.That(connection.ChannelId, Is.EqualTo($"channel-{i}"));
        Assert.That(instance.QueueApproximateCount, Is.EqualTo(maxConcurrentCount - i + maxConcurrentCount));
        Assert.That(instance.AvailableConnectionCount, Is.EqualTo(maxConcurrentCount - i));
        Assert.That(instance.OutstandingRequestCount, Is.EqualTo(i));
      }

      // Should not give back the hidden connections
      for (var i = 1; i <= maxConcurrentCount; i++)
      {
        Assert.That(instance.TryGetConnection(out var connection), Is.False);
        Assert.That(connection, Is.Null);
        Assert.That(instance.QueueApproximateCount, Is.EqualTo(maxConcurrentCount));
        Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));
        Assert.That(instance.OutstandingRequestCount, Is.EqualTo(maxConcurrentCount));
      }
    }

    [Test]
    public async Task AddConnection_ImmediateDispatchShouldCountAsOutstanding()
    {
      var maxConcurrentCount = 10;
      var lambdaClient = new Mock<IAmazonLambda>();
      var instance = new LambdaInstance(maxConcurrentCount, "somefunc", null, lambdaClient.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      // Start the Lambda
      await instance.Start();

      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));

      // These should not be visible but should count as outstanding
      for (var i = 1; i <= maxConcurrentCount; i++)
      {
        Assert.That(instance.AddConnection(request.Object, response.Object, $"channel-{i}", immediateDispatch: true), Is.Not.Null);
        Assert.That(instance.QueueApproximateCount, Is.EqualTo(0));
        Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));
        Assert.That(instance.OutstandingRequestCount, Is.EqualTo(i));
      }
    }

    [Test]
    public async Task AddConnection_ShouldRemoveAndReaddConnections()
    {
      var maxConcurrentCount = 10;
      var lambdaClient = new Mock<IAmazonLambda>();
      var instance = new LambdaInstance(maxConcurrentCount, "someFunc", null, lambdaClient.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      // Start the Lambda
      await instance.Start();

      await instance.AddConnection(request.Object, response.Object, "channel-1", false);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(1));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      await instance.AddConnection(request.Object, response.Object, "channel-2", false);
      await instance.AddConnection(request.Object, response.Object, "channel-3", false);
      await instance.AddConnection(request.Object, response.Object, "channel-4", false);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      Assert.That(instance.TryGetConnection(out var connection), Is.True);
      Assert.That(connection, Is.Not.Null);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(3));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(3));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(1));

      // Reinstate the connection
      await instance.ReenqueueUnusedConnection(connection);
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
    }
  }
}