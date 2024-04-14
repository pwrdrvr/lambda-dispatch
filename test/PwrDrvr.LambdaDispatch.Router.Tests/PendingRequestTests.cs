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

      Assert.AreEqual(mockRequest.Object, pendingRequest.Request);
      Assert.AreEqual(mockResponse.Object, pendingRequest.Response);
      Assert.IsFalse(pendingRequest.Dispatched);
      Assert.IsNotNull(pendingRequest.ResponseFinishedTCS);
      Assert.IsNotNull(pendingRequest.GatewayTimeoutCTS);
    }

    [Test]
    public void TestAbort()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.IsFalse(pendingRequest.GatewayTimeoutCTS.IsCancellationRequested);

      pendingRequest.Abort();

      Assert.IsTrue(pendingRequest.GatewayTimeoutCTS.IsCancellationRequested);
    }

    [Test]
    public void TestRecordDispatchTime()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.IsFalse(pendingRequest.Dispatched);

      pendingRequest.RecordDispatchTime();

      Assert.IsTrue(pendingRequest.Dispatched);
    }

    [Test]
    public void TestDispatchDelay()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.IsTrue(pendingRequest.DispatchDelay.TotalMilliseconds >= 0);
    }

    [Test]
    public void TestDuration()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.IsTrue(pendingRequest.Duration.TotalMilliseconds >= 0);
    }

    [Test]
    public void TestDurationAfterDispatch()
    {
      var mockRequest = new Mock<HttpRequest>();
      var mockResponse = new Mock<HttpResponse>();

      var pendingRequest = new PendingRequest(mockRequest.Object, mockResponse.Object);

      Assert.IsTrue(pendingRequest.DurationAfterDispatch.TotalMilliseconds >= 0);

      pendingRequest.RecordDispatchTime();

      Assert.IsTrue(pendingRequest.DurationAfterDispatch.TotalMilliseconds >= 0);
    }
  }
}