using Moq;
using NUnit.Framework;
using System;
using System.Threading;
using PwrDrvr.LambdaDispatch.Router;
using Microsoft.AspNetCore.Http;

namespace PwrDrvr.LambdaDispatch.Router.Tests
{
  public class PendingRequestTests
  {
    [Test]
    public void TestPendingRequestProperties()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);
      Assert.Multiple(() =>
      {
        Assert.That(pendingRequest.Request, Is.EqualTo(mockRequest.Object));
        Assert.That(pendingRequest.Response, Is.EqualTo(mockResponse.Object));
        Assert.That(pendingRequest.Dispatched, Is.False);
        Assert.That(pendingRequest.ResponseFinishedTCS, Is.Not.Null);
        Assert.That(pendingRequest.GatewayTimeoutCTS, Is.Not.Null);
      });
    }

    [Test]
    public void TestAbort()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.That(pendingRequest.GatewayTimeoutCTS.IsCancellationRequested, Is.False);

      pendingRequest.Abort();

      Assert.That(pendingRequest.GatewayTimeoutCTS.IsCancellationRequested, Is.True);
    }

    [Test]
    public void TestRecordDispatchTime()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.That(pendingRequest.Dispatched, Is.False);

      pendingRequest.RecordDispatchTime();

      Assert.That(pendingRequest.Dispatched, Is.True);
    }

    [Test]
    public void TestDispatchDelay()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.That(pendingRequest.DispatchDelay.TotalMilliseconds >= 0, Is.True);
    }

    [Test]
    public void TestDuration()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.That(pendingRequest.Duration.TotalMilliseconds >= 0, Is.True);
    }

    [Test]
    [Retry(3)]
    public void TestDurationAfterDispatch()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.That(pendingRequest.DurationAfterDispatch.TotalMilliseconds >= 0, Is.True);

      pendingRequest.RecordDispatchTime();

      Assert.That(pendingRequest.DurationAfterDispatch.TotalMilliseconds >= 0, Is.True);
    }
  }
}