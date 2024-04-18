using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace PwrDrvr.LambdaDispatch.Router.ControlChannels.Controllers;

[Area("ControlChannels")]
[Route("/reset")]
public class ResetController : ControllerBase
{
  private readonly IMetricsRegistry _metricsRegistry;

  public ResetController(IMetricsRegistry metricsRegistry)
  {
    _metricsRegistry = metricsRegistry;
  }

  public void HandleRequest()
  {
    _metricsRegistry.Reset();
    Ok();
  }
}