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
        Assert.That(pendingRequest.Dispatched, Is.False);
        Assert.That(pendingRequest.ResponseFinishedTCS, Is.Not.Null);
        Assert.That(pendingRequest.GatewayTimeoutCTS, Is.Not.Null);
      });

      // Dispatch the request
      var dispatched = pendingRequest.Dispatch(out var incomingRequest, out var incomingResponse);
      Assert.Multiple(() =>
      {
        Assert.That(dispatched, Is.True);
        Assert.That(incomingRequest, Is.EqualTo(mockRequest.Object));
        Assert.That(incomingResponse, Is.EqualTo(mockResponse.Object));
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

      pendingRequest.Dispatch(out var _, out var _);

      Assert.That(pendingRequest.Dispatched, Is.True);
    }

    [Test]
    public void TestDispatchDelay()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.That(pendingRequest.DispatchDelay.TotalMilliseconds, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void TestDuration()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.That(pendingRequest.Duration.TotalMilliseconds, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    [Retry(3)]
    public void TestDurationAfterDispatch()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.That(pendingRequest.DurationAfterDispatch.TotalMilliseconds, Is.GreaterThanOrEqualTo(0));

      pendingRequest.Dispatch(out var incomingRequest, out var incomingResponse);

      Assert.That(pendingRequest.DurationAfterDispatch.TotalMilliseconds, Is.GreaterThanOrEqualTo(0));
    }
  }
}