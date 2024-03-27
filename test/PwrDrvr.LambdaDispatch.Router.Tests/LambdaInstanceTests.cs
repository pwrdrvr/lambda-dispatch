using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Microsoft.AspNetCore.Http;

namespace PwrDrvr.LambdaDispatch.Router.Tests;

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
  public async Task AddConnection_ImmediateDispatch()
  {
    var maxConcurrentCount = 10;
    var lambdaClient = new Mock<IAmazonLambda>();
    var dispatcher = new Mock<IBackgroundDispatcher>();
    var instance = new LambdaInstance(maxConcurrentCount, "somefunc", null, lambdaClient.Object, dispatcher.Object);

    var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
    var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
    request.Setup(i => i.HttpContext).Returns(requestContext.Object);
    var response = new Mock<HttpResponse>();
    int statusCode = 0;
    response.Setup(i => i.StartAsync(default)).Returns(Task.CompletedTask);
    response.SetupSet(i => i.StatusCode = It.IsAny<int>())
      .Callback<int>(value =>
      {
        statusCode = value;
      });

    GetCallbackIP.Init(1000, "https", "127.0.0.1");

    // Start the Lambda
    instance.Start();
    Assert.Multiple(() =>
    {
      Assert.That(instance.State, Is.EqualTo(LambdaInstanceState.Starting));
      Assert.That(instance.IsOpen, Is.False);
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));
    });

    // These should not be visible but should count as outstanding
    for (var i = 1; i <= maxConcurrentCount; i++)
    {
      statusCode = 0;
      var result = await instance.AddConnection(request.Object, response.Object, $"channel-{i}", AddConnectionDispatchMode.ImmediateDispatch);
      Assert.Multiple(() =>
      {
        Assert.That(instance.State, Is.EqualTo(LambdaInstanceState.Open));
        Assert.That(instance.IsOpen, Is.True);
        Assert.That(result.CanUseNow, Is.True, $"Failed on iteration {i}: Expected result.CanUseNow to be true");
        Assert.That(result.WasRejected, Is.False, $"Failed on iteration {i}: Expected result.WasRejected to be false");
        Assert.That(result.Connection, Is.Not.Null, $"Failed on iteration {i}: Expected result.Connection to not be null");
        response.Verify(i => i.StartAsync(default), Times.Exactly(i));
        Assert.That(statusCode, Is.EqualTo(200), $"Failed on iteration {i}: Expected statusCode to be 200");
        Assert.That(instance.QueueApproximateCount, Is.EqualTo(0), $"Failed on iteration {i}: Expected instance.QueueApproximateCount to be 0");
        Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0), $"Failed on iteration {i}: Expected instance.AvailableConnectionCount to be 0");
        Assert.That(instance.OutstandingRequestCount, Is.EqualTo(i), $"Failed on iteration {i}: Expected instance.OutstandingRequestCount to be {i}");
      });
    }

    // Add one more
    // This connection should be hidden and we should be told not to use it
    statusCode = 0;
    var result2 = await instance.AddConnection(request.Object, response.Object, "channel-11", AddConnectionDispatchMode.ImmediateDispatch);
    Assert.Multiple(() =>
    {
      Assert.That(result2.CanUseNow, Is.False);
      Assert.That(result2.WasRejected, Is.False);
      Assert.That(result2.Connection, Is.Not.Null);
      Assert.That(statusCode, Is.EqualTo(200));
      response.Verify(i => i.StartAsync(default), Times.Exactly(maxConcurrentCount + 1));
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(maxConcurrentCount));
    });
  }

  [Test]
  public async Task AddConnection_TentativeDispatch()
  {
    var maxConcurrentCount = 10;
    var lambdaClient = new Mock<IAmazonLambda>();
    var dispatcher = new Mock<IBackgroundDispatcher>();
    var instance = new LambdaInstance(maxConcurrentCount, "somefunc", null, lambdaClient.Object, dispatcher.Object);

    var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
    var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
    request.Setup(i => i.HttpContext).Returns(requestContext.Object);
    var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();
    int statusCode = 0;
    response.Setup(i => i.StartAsync(default)).Returns(Task.CompletedTask);
    response.SetupSet(i => i.StatusCode = It.IsAny<int>())
      .Callback<int>(value =>
      {
        statusCode = value;
      });

    GetCallbackIP.Init(1000, "https", "127.0.0.1");

    // Start the Lambda
    instance.Start();
    Assert.Multiple(() =>
    {
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));
    });

    // These should not be visible but should count as outstanding
    for (var i = 1; i <= maxConcurrentCount; i++)
    {
      statusCode = 0;
      var result = await instance.AddConnection(request.Object, response.Object, $"channel-{i}", AddConnectionDispatchMode.TentativeDispatch);
      Assert.Multiple(() =>
      {
        Assert.That(result.CanUseNow, Is.True, $"Failed on iteration {i}: Expected result.CanUseNow to be true");
        Assert.That(result.WasRejected, Is.False, $"Failed on iteration {i}: Expected result.WasRejected to be false");
        Assert.That(result.Connection, Is.Not.Null, $"Failed on iteration {i}: Expected result.Connection to not be null");
        response.Verify(i => i.StartAsync(default), Times.Exactly(i));
        Assert.That(statusCode, Is.EqualTo(200), $"Failed on iteration {i}: Expected statusCode to be 200");
        Assert.That(instance.QueueApproximateCount, Is.EqualTo(0), $"Failed on iteration {i}: Expected instance.QueueApproximateCount to be 0");
        Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0), $"Failed on iteration {i}: Expected instance.AvailableConnectionCount to be 0");
        Assert.That(instance.OutstandingRequestCount, Is.EqualTo(i), $"Failed on iteration {i}: Expected instance.OutstandingRequestCount to be {i}");
      });
    }

    // Add one more
    statusCode = 0;
    var result2 = await instance.AddConnection(request.Object, response.Object, "channel-11", AddConnectionDispatchMode.TentativeDispatch);
    Assert.Multiple(() =>
    {
      Assert.That(result2.CanUseNow, Is.False);
      Assert.That(result2.WasRejected, Is.False);
      Assert.That(result2.Connection, Is.Not.Null);
      response.Verify(i => i.StartAsync(default), Times.Exactly(maxConcurrentCount + 1));
      Assert.That(statusCode, Is.EqualTo(200));
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(0));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(maxConcurrentCount));
    });
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

    Assert.Multiple(() =>
    {
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(1));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
    });
    await instance.AddConnection(request.Object, response.Object, "channel-2", AddConnectionDispatchMode.Enqueue);
    await instance.AddConnection(request.Object, response.Object, "channel-3", AddConnectionDispatchMode.Enqueue);
    await instance.AddConnection(request.Object, response.Object, "channel-4", AddConnectionDispatchMode.Enqueue);
    Assert.Multiple(() =>
    {
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
    });
    var result = instance.TryGetConnection(out var connection);
    Assert.Multiple(() =>
    {
      Assert.That(result, Is.True);
      Assert.That(connection, Is.Not.Null);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(3));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(3));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(1));
    });

    // Reinstate the connection
    instance.ReenqueueUnusedConnection(connection);
    Assert.Multiple(() =>
    {
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(0));
    });
  }
}