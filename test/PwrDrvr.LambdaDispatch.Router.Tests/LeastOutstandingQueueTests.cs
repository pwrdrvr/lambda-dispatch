using Amazon.Lambda;
using Moq;

namespace PwrDrvr.LambdaDispatch.Router.Tests
{
  public class LeastOutstandingQueueTests
  {
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void Constructor_ShouldRejectTooSmallMaxConcurrentCount()
    {
      var config = new Mock<IConfig>();
      config.Setup(c => c.MaxConcurrentCount).Returns(0);
      Assert.Throws<ArgumentOutOfRangeException>(() => new LeastOutstandingQueue(config.Object));
      config.Setup(c => c.MaxConcurrentCount).Returns(-1);
      Assert.Throws<ArgumentOutOfRangeException>(() => new LeastOutstandingQueue(config.Object));
    }

    [Test]
    public void AddInstance_ShouldRejectNullInstance()
    {
      var maxConcurrentCount = 10;
      var config = new Mock<IConfig>();
      config.Setup(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
      using var queue = new LeastOutstandingQueue(config.Object);

      Assert.Throws<ArgumentNullException>(() => queue.AddInstance(null));
    }

    [Test]
    public void AddInstance_ShouldNotReturnInvlaidInstanceFromQueue()
    {
      var maxConcurrentCount = 10;
      var config = new Mock<IConfig>();
      config.Setup(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
      using var queue = new LeastOutstandingQueue(config.Object);
      var lambdaClient = new Mock<IAmazonLambda>();
      var dispatcher = new Mock<IBackgroundDispatcher>();
      var getCallbackIP = new Mock<IGetCallbackIP>();
      getCallbackIP.Setup(i => i.CallbackUrl).Returns("https://127.0.0.1:1000");

      var instance = new LambdaInstance(maxConcurrentCount: maxConcurrentCount,
            functionName: "someFunc",
            poolId: "default",
            lambdaClient: lambdaClient.Object,
            dispatcher: dispatcher.Object,
            getCallbackIP: getCallbackIP.Object
            );
      queue.AddInstance(instance);

      var result = queue.TryGetConnection(out var connection);

      Assert.That(result, Is.False);
      Assert.That(connection, Is.Null);
    }

    [Test]
    public void TryGetLeastOutstandingConnection_ShouldReturnConnection()
    {
      var maxConcurrentCount = 10;

      var config = new Mock<IConfig>();
      config.Setup(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      var instance = new Mock<ILambdaInstance>();
      var connection = new Mock<LambdaConnection>(request.Object, response.Object, instance.Object, "channel-1", false);

      var id = "instance-1";
      instance.Setup(i => i.Id).Returns(id);
      instance.Setup(i => i.State).Returns(LambdaInstanceState.Open);
      instance.Setup(i => i.IsOpen).Returns(true);
      instance.Setup(i => i.AvailableConnectionCount).Returns(1);
      var connectionObject = connection.Object;
      instance.Setup(i => i.TryGetConnection(out connectionObject, false)).Returns(true);

      using var queue = new LeastOutstandingQueue(config.Object);

      // Add the instance
      queue.AddInstance(instance.Object);

      var result = queue.TryGetConnection(out var dequeuedConnection);

      Assert.That(result, Is.True);
      Assert.That(dequeuedConnection, Is.Not.Null);

      // Getting another instance should fail
      instance.Setup(i => i.OutstandingRequestCount).Returns(1);
      instance.Setup(i => i.AvailableConnectionCount).Returns(0);
      // TryGetConnection should not be called
      instance.Setup(i => i.TryGetConnection(out connectionObject, false)).Throws<Exception>();
      result = queue.TryGetConnection(out dequeuedConnection);

      Assert.That(result, Is.False);
      Assert.That(dequeuedConnection, Is.Null);
    }


    [Test]
    public void TryGetLeastOutstandingConnection_WorksWithMaxConcurrentOne()
    {
      var maxConcurrentCount = 1;
      var config = new Mock<IConfig>();
      config.Setup(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
      using var queue = new LeastOutstandingQueue(config.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      var instance = new Mock<ILambdaInstance>();
      var connection = new Mock<LambdaConnection>(request.Object, response.Object, instance.Object, "channel-1", false);

      var id = "instance-1";
      instance.Setup(i => i.Id).Returns(id);
      instance.Setup(i => i.State).Returns(LambdaInstanceState.Open);
      instance.Setup(i => i.IsOpen).Returns(true);
      instance.Setup(i => i.AvailableConnectionCount).Returns(1);
      instance.Setup(i => i.OutstandingRequestCount).Returns(0);
      var connectionObject = connection.Object;
      instance.Setup(i => i.TryGetConnection(out connectionObject, false)).Returns(true);

      // Add the instance
      queue.AddInstance(instance.Object);

      var result = queue.TryGetConnection(out var dequeuedConnection);

      Assert.That(result, Is.True);
      Assert.That(dequeuedConnection, Is.Not.Null);

      // Getting another instance should fail
      instance.Setup(i => i.OutstandingRequestCount).Returns(1);
      instance.Setup(i => i.TryGetConnection(out connectionObject, false)).Returns(false);
      result = queue.TryGetConnection(out dequeuedConnection);

      Assert.That(result, Is.False);
      Assert.That(dequeuedConnection, Is.Null);
    }

    [Test]
    public void TryGetLeastOutstandingConnection_NoAvailableInstance()
    {
      var maxConcurrentCount = 1;
      var config = new Mock<IConfig>();
      config.Setup(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
      using var queue = new LeastOutstandingQueue(config.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      var instance = new Mock<ILambdaInstance>();
      var connection = new Mock<LambdaConnection>(request.Object, response.Object, instance.Object, "channel-1", false);

      var id = "instance-1";
      instance.Setup(i => i.Id).Returns(id);
      instance.Setup(i => i.State).Returns(LambdaInstanceState.Open);
      instance.Setup(i => i.IsOpen).Returns(true);
      instance.Setup(i => i.AvailableConnectionCount).Returns(0);
      instance.Setup(i => i.OutstandingRequestCount).Returns(1);
      var connectionObject = connection.Object;
      instance.Setup(i => i.TryGetConnection(out connectionObject, false)).Returns(true);

      // Add the instance
      queue.AddInstance(instance.Object);

      var result = queue.TryGetConnection(out var dequeuedConnection);
      Assert.Multiple(() =>
      {
        Assert.That(result, Is.False);
        Assert.That(dequeuedConnection, Is.Null);
      });

      // Reinstate the instance
      instance.Setup(i => i.AvailableConnectionCount).Returns(1);
      instance.Setup(i => i.OutstandingRequestCount).Returns(0);
      // We should find the instance in the full list and move it to the available list
      Assert.That(queue.ReinstateFullInstance(instance.Object), Is.True);

      result = queue.TryGetConnection(out dequeuedConnection);
      Assert.Multiple(() =>
      {
        Assert.That(result, Is.True);
        Assert.That(dequeuedConnection, Is.Not.Null);
      });
    }

    [Test]
    public void TryGetLeastOutstandingConnection_ShouldMarkNoAvailableAsFull()
    {
      var maxConcurrentCount = 10;
      var config = new Mock<IConfig>();
      config.Setup(c => c.MaxConcurrentCount).Returns(maxConcurrentCount);
      using var queue = new LeastOutstandingQueue(config.Object);

      var requestContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
      var request = new Mock<Microsoft.AspNetCore.Http.HttpRequest>();
      request.Setup(i => i.HttpContext).Returns(requestContext.Object);
      var response = new Mock<Microsoft.AspNetCore.Http.HttpResponse>();

      var instance = new Mock<ILambdaInstance>();
      var connection = new Mock<LambdaConnection>(request.Object, response.Object, instance.Object, "channel-1", false);

      var id = "instance-1";
      instance.Setup(i => i.Id).Returns(id);
      instance.Setup(i => i.State).Returns(LambdaInstanceState.Open);
      instance.Setup(i => i.IsOpen).Returns(true);
      instance.SetupSequence(i => i.AvailableConnectionCount).Returns(1).Returns(1).Returns(0);
      instance.Setup(i => i.OutstandingRequestCount).Returns(1);
      var connectionObject = connection.Object;
      instance.Setup(i => i.TryGetConnection(out connectionObject, false)).Returns(true);

      // Add the instance
      queue.AddInstance(instance.Object);

      var result = queue.TryGetConnection(out var dequeuedConnection);
      Assert.Multiple(() =>
      {
        Assert.That(result, Is.True);
        Assert.That(dequeuedConnection, Is.Not.Null);
      });

      // Reinstate the instance
      instance.Setup(i => i.AvailableConnectionCount).Returns(1);
      instance.Setup(i => i.OutstandingRequestCount).Returns(0);
      // We should find the instance in the full list and move it to the available list
      Assert.That(queue.ReinstateFullInstance(instance.Object), Is.True);
    }
  }
}