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
      var lambdaClient = new Mock<IAmazonLambda>();
      var dispatcher = new Mock<IBackgroundDispatcher>();

      Assert.Throws<ArgumentOutOfRangeException>(() => new LambdaInstance(0, "somefunc", "$LATEST", lambdaClient.Object, dispatcher.Object));
      Assert.Throws<ArgumentOutOfRangeException>(() => new LambdaInstance(-1, "somefunc", "$LATEST", lambdaClient.Object, dispatcher.Object));
    }

    [Test]
    public async Task AddConnection_ShouldAddAndCountConnections()
    {
      var maxConcurrentCount = 10;
      var lambdaClient = new Mock<IAmazonLambda>();
      var dispatcher = new Mock<IBackgroundDispatcher>();
      var instance = new LambdaInstance(maxConcurrentCount, "somefunc", null, lambdaClient.Object, dispatcher.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      GetCallbackIP.Init(1000, "https", "127.0.0.1");

      // Start the Lambda
      instance.Start();

      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));

      Assert.That(instance.AddConnection(request.Object, response.Object, "channel-1", AddConnectionDispatchMode.Enqueue), Is.Not.Null);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(1));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      await instance.AddConnection(request.Object, response.Object, "channel-2", AddConnectionDispatchMode.Enqueue);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(2));
      await instance.AddConnection(request.Object, response.Object, "channel-3", AddConnectionDispatchMode.Enqueue);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(3));
      await instance.AddConnection(request.Object, response.Object, "channel-4", AddConnectionDispatchMode.Enqueue);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      await instance.AddConnection(request.Object, response.Object, "channel-5", AddConnectionDispatchMode.Enqueue);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(5));
      await instance.AddConnection(request.Object, response.Object, "channel-6", AddConnectionDispatchMode.Enqueue);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(6));
      await instance.AddConnection(request.Object, response.Object, "channel-7", AddConnectionDispatchMode.Enqueue);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(7));
      await instance.AddConnection(request.Object, response.Object, "channel-8", AddConnectionDispatchMode.Enqueue);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(8));
      await instance.AddConnection(request.Object, response.Object, "channel-9", AddConnectionDispatchMode.Enqueue);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(9));
      await instance.AddConnection(request.Object, response.Object, "channel-10", AddConnectionDispatchMode.Enqueue);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(10));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(10));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      // Can have more connections than maxConcurrentCount?
      // This kinda makes sense
      await instance.AddConnection(request.Object, response.Object, "channel-11", AddConnectionDispatchMode.Enqueue);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(11));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(10));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
    }

    [Test]
    public void AddConnection_ShouldHideSurplusConnections()
    {
      var maxConcurrentCount = 10;
      var lambdaClient = new Mock<IAmazonLambda>();
      var dispatcher = new Mock<IBackgroundDispatcher>();
      var instance = new LambdaInstance(maxConcurrentCount, "somefunc", null, lambdaClient.Object, dispatcher.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      GetCallbackIP.Init(1000, "https", "127.0.0.1");

      // Start the Lambda
      instance.Start();

      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));

      // These should be visible
      for (var i = 1; i <= maxConcurrentCount; i++)
      {
        Assert.That(instance.AddConnection(request.Object, response.Object, $"channel-{i}", AddConnectionDispatchMode.Enqueue), Is.Not.Null);
        Assert.That(instance.QueueApproximateCount, Is.EqualTo(i));
        Assert.That(instance.AvailableConnectionCount, Is.EqualTo(i));
        Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      }

      // These should be hidden
      for (var i = 1; i <= maxConcurrentCount; i++)
      {
        Assert.That(instance.AddConnection(request.Object, response.Object, $"channel-{i + 10}", AddConnectionDispatchMode.Enqueue), Is.Not.Null);
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
    public void AddConnection_ImmediateDispatchShouldCountAsOutstanding()
    {
      var maxConcurrentCount = 10;
      var lambdaClient = new Mock<IAmazonLambda>();
      var dispatcher = new Mock<IBackgroundDispatcher>();
      var instance = new LambdaInstance(maxConcurrentCount, "somefunc", null, lambdaClient.Object, dispatcher.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      GetCallbackIP.Init(1000, "https", "127.0.0.1");

      // Start the Lambda
      instance.Start();

      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));

      // These should not be visible but should count as outstanding
      for (var i = 1; i <= maxConcurrentCount; i++)
      {
        Assert.That(instance.AddConnection(request.Object, response.Object, $"channel-{i}", AddConnectionDispatchMode.ImmediateDispatch), Is.Not.Null);
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
      var dispatcher = new Mock<IBackgroundDispatcher>();
      var instance = new LambdaInstance(maxConcurrentCount, "someFunc", null, lambdaClient.Object, dispatcher.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      GetCallbackIP.Init(1000, "https", "127.0.0.1");

      // Start the Lambda
      instance.Start();

      await instance.AddConnection(request.Object, response.Object, "channel-1", AddConnectionDispatchMode.Enqueue);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(1));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      await instance.AddConnection(request.Object, response.Object, "channel-2", AddConnectionDispatchMode.Enqueue);
      await instance.AddConnection(request.Object, response.Object, "channel-3", AddConnectionDispatchMode.Enqueue);
      await instance.AddConnection(request.Object, response.Object, "channel-4", AddConnectionDispatchMode.Enqueue);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));

      Assert.That(instance.TryGetConnection(out var connection), Is.True);
      Assert.That(connection, Is.Not.Null);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(3));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(3));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(1));

      // Reinstate the connection
      instance.ReenqueueUnusedConnection(connection);
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
    }
  }
}