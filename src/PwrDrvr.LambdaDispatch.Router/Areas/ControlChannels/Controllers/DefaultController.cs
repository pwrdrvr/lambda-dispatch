using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace PwrDrvr.LambdaDispatch.Router.ControlChannels.Controllers;

[Area("ControlChannels")]
[Route("{*url}")]
public class DefaultController : ControllerBase
{
  public async Task HandleRequest()
  {
    Console.WriteLine("Router.ControlChannels.Controllers.DefaultController.HandleRequest - Start");
    Response.StatusCode = 404;
    await Response.WriteAsync("No matching Control Channel route found");
  }
}