using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace PwrDrvr.LambdaDispatch.Router;


[Route("/reset")]
public class ResetController : ControllerBase
{
  private readonly Dispatcher dispatcher;

  public ResetController(Dispatcher dispatcher)
  {
    this.dispatcher = dispatcher;
  }

  public void HandleRequest()
  {
    MetricsRegistry.Reset();
    Ok();
  }
}