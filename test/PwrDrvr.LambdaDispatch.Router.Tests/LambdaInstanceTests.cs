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

      Assert.That(instance.AddConnection(request.Object, response.Object, "channel-1", false), Is.Not.Null);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(1));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(1));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(9));

      await instance.AddConnection(request.Object, response.Object, "channel-2", false);
      await instance.AddConnection(request.Object, response.Object, "channel-3", false);
      await instance.AddConnection(request.Object, response.Object, "channel-4", false);
      await instance.AddConnection(request.Object, response.Object, "channel-5", false);
      await instance.AddConnection(request.Object, response.Object, "channel-6", false);
      await instance.AddConnection(request.Object, response.Object, "channel-7", false);
      await instance.AddConnection(request.Object, response.Object, "channel-8", false);
      await instance.AddConnection(request.Object, response.Object, "channel-9", false);
      await instance.AddConnection(request.Object, response.Object, "channel-10", false);

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
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(9));

      await instance.AddConnection(request.Object, response.Object, "channel-2", false);
      await instance.AddConnection(request.Object, response.Object, "channel-3", false);
      await instance.AddConnection(request.Object, response.Object, "channel-4", false);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(6));

      Assert.That(instance.TryGetConnection(out var connection), Is.True);
      Assert.That(connection, Is.Not.Null);

      Assert.That(instance.QueueApproximateCount, Is.EqualTo(3));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(3));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(7));

      // Reinstate the connection
      await instance.ReenqueueUnusedConnection(connection);
      Assert.That(instance.QueueApproximateCount, Is.EqualTo(4));
      Assert.That(instance.AvailableConnectionCount, Is.EqualTo(4));
      Assert.That(instance.OutstandingRequestCount, Is.EqualTo(6));
    }
  }
}