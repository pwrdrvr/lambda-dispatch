using Amazon.Lambda;
using Amazon.Lambda.Model;
using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;

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
      Assert.Throws<ArgumentOutOfRangeException>(() => new LeastOutstandingQueue(0));
      Assert.Throws<ArgumentOutOfRangeException>(() => new LeastOutstandingQueue(-1));
    }

    [Test]
    public void Constructor_ShouldAcceptMaxConcurrentCount()
    {
      var maxConcurrentCount = 10;
      using var queue = new LeastOutstandingQueue(maxConcurrentCount);

      Assert.That(queue.MaxConcurrentCount, Is.EqualTo(maxConcurrentCount));
    }

    [Test]
    public void AddInstance_ShouldRejectNullInstance()
    {
      var maxConcurrentCount = 10;
      using var queue = new LeastOutstandingQueue(maxConcurrentCount);

      Assert.Throws<ArgumentNullException>(() => queue.AddInstance(null));
    }

    [Test]
    public void AddInstance_ShouldNotReturnInvlaidInstanceFromQueue()
    {
      var maxConcurrentCount = 10;
      using var queue = new LeastOutstandingQueue(maxConcurrentCount);
      var lambdaClient = new Mock<IAmazonLambda>();
      var dispatcher = new Mock<IBackgroundDispatcher>();

      var instance = new LambdaInstance(maxConcurrentCount, "someFunc", null, lambdaClient.Object, dispatcher.Object);
      queue.AddInstance(instance);

      var result = queue.TryGetLeastOustandingConnection(out var connection);

      Assert.That(result, Is.False);
      Assert.That(connection, Is.Null);
    }

    [Test]
    public void TryGetLeastOutstandingConnection_ShouldReturnConnection()
    {
      var maxConcurrentCount = 10;
      using var queue = new LeastOutstandingQueue(maxConcurrentCount);

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

      // Add the instance
      queue.AddInstance(instance.Object);

      var result = queue.TryGetLeastOustandingConnection(out var dequeuedConnection);

      Assert.IsTrue(result);
      Assert.IsNotNull(dequeuedConnection);

      // Getting another instance should fail
      instance.Setup(i => i.OutstandingRequestCount).Returns(1);
      instance.Setup(i => i.AvailableConnectionCount).Returns(0);
      // TryGetConnection should not be called
      instance.Setup(i => i.TryGetConnection(out connectionObject, false)).Throws<Exception>();
      result = queue.TryGetLeastOustandingConnection(out dequeuedConnection);

      Assert.That(result, Is.False);
      Assert.IsNull(dequeuedConnection);
    }


    [Test]
    public void TryGetLeastOutstandingConnection_WorksWithMaxConcurrentOne()
    {
      var maxConcurrentCount = 1;
      using var queue = new LeastOutstandingQueue(maxConcurrentCount);

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

      var result = queue.TryGetLeastOustandingConnection(out var dequeuedConnection);

      Assert.That(result, Is.True);
      Assert.That(dequeuedConnection, Is.Not.Null);

      // Getting another instance should fail
      instance.Setup(i => i.OutstandingRequestCount).Returns(1);
      instance.Setup(i => i.TryGetConnection(out connectionObject, false)).Returns(false);
      result = queue.TryGetLeastOustandingConnection(out dequeuedConnection);

      Assert.That(result, Is.False);
      Assert.That(dequeuedConnection, Is.Null);
    }

    [Test]
    public void TryGetLeastOutstandingConnection_NoAvailableInstance()
    {
      var maxConcurrentCount = 1;
      using var queue = new LeastOutstandingQueue(maxConcurrentCount);

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

      var result = queue.TryGetLeastOustandingConnection(out var dequeuedConnection);
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

      result = queue.TryGetLeastOustandingConnection(out dequeuedConnection);
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
      using var queue = new LeastOutstandingQueue(maxConcurrentCount);

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

      var result = queue.TryGetLeastOustandingConnection(out var dequeuedConnection);
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