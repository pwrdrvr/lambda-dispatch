using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using PwrDrvr.LambdaDispatch.Router.ControlChannels.Controllers;

namespace PwrDrvr.LambdaDispatch.Router.ControlChannels.Tests;

public class DefaultControllerTests
{
  private DefaultController _controller;
  private DefaultHttpContext _httpContext;

  [SetUp]
  public void SetUp()
  {
    _httpContext = new DefaultHttpContext();
    _httpContext.Response.Body = new MemoryStream();

    _controller = new DefaultController
    {
      ControllerContext = new ControllerContext
      {
        HttpContext = _httpContext
      }
    };
  }

  [Test]
  public async Task HandleRequest_SetsStatusCodeTo404()
  {
    await _controller.HandleRequest();

    Assert.That(_httpContext.Response.StatusCode, Is.EqualTo(404));
  }

  [Test]
  public async Task HandleRequest_WritesErrorMessageToResponse()
  {
    await _controller.HandleRequest();

    _httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
    var responseBody = await new StreamReader(_httpContext.Response.Body).ReadToEndAsync();

    Assert.That(responseBody, Is.EqualTo("No matching Control Channel route found"));
  }
}