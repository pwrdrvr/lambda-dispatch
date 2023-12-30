using Moq;
using NUnit.Framework;
using PwrDrvr.LambdaDispatch.Router;

namespace PwrDrvr.LambdaDispatch.Tests
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
      var instance = new LambdaInstance(maxConcurrentCount);
      queue.AddInstance(instance);

      var result = queue.TryGetLeastOustandingConnection(out var connection);

      Assert.IsFalse(result);
      Assert.IsNull(connection);
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
      var connection = new Mock<LambdaConnection>(request.Object, response.Object, instance.Object, "channel-1");

      var id = "instance-1";
      instance.Setup(i => i.Id).Returns(id);
      instance.Setup(i => i.State).Returns(LambdaInstanceState.Open);
      instance.Setup(i => i.AvailableConnectionCount).Returns(1);
      var connectionObject = connection.Object;
      instance.Setup(i => i.TryGetConnection(out connectionObject)).Returns(true);

      // Add the instance
      queue.AddInstance(instance.Object);

      var result = queue.TryGetLeastOustandingConnection(out var dequeuedConnection);

      Assert.IsTrue(result);
      Assert.IsNotNull(dequeuedConnection);
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
      var connection = new Mock<LambdaConnection>(request.Object, response.Object, instance.Object, "channel-1");

      var id = "instance-1";
      instance.Setup(i => i.Id).Returns(id);
      instance.Setup(i => i.State).Returns(LambdaInstanceState.Open);
      instance.Setup(i => i.AvailableConnectionCount).Returns(1);
      instance.Setup(i => i.OutstandingRequestCount).Returns(0);
      var connectionObject = connection.Object;
      instance.Setup(i => i.TryGetConnection(out connectionObject)).Returns(true);

      // Add the instance
      queue.AddInstance(instance.Object);

      var result = queue.TryGetLeastOustandingConnection(out var dequeuedConnection);

      Assert.IsTrue(result);
      Assert.IsNotNull(dequeuedConnection);
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
      var connection = new Mock<LambdaConnection>(request.Object, response.Object, instance.Object, "channel-1");

      var id = "instance-1";
      instance.Setup(i => i.Id).Returns(id);
      instance.Setup(i => i.State).Returns(LambdaInstanceState.Open);
      instance.Setup(i => i.AvailableConnectionCount).Returns(0);
      instance.Setup(i => i.OutstandingRequestCount).Returns(1);
      var connectionObject = connection.Object;
      instance.Setup(i => i.TryGetConnection(out connectionObject)).Returns(true);

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
      queue.ReinstateFullInstance(instance.Object);

      result = queue.TryGetLeastOustandingConnection(out dequeuedConnection);
      Assert.Multiple(() =>
      {
        Assert.That(result, Is.True);
        Assert.That(dequeuedConnection, Is.Not.Null);
      });
    }
  }
}