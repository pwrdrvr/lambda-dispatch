using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace PwrDrvr.LambdaDispatch.Router;


[Route("{*url:regex(^((?!api/chunked).)*$)}")]
public class IncomingController : ControllerBase
{
  private readonly Dispatcher dispatcher;
  private readonly ILogger<IncomingController> logger;

  public IncomingController(Dispatcher dispatcher, ILogger<IncomingController> logger)
  {
    this.dispatcher = dispatcher;
    this.logger = logger;
  }

  [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")]
  public async Task HandleRequest()
  {
    logger.LogInformation("Router.IncomingController.HandleRequest - Start");
    using (MetricsRegistry.Metrics.Measure.Timer.Time(MetricsRegistry.IncomingRequestTimer))
    {
      if (Request.Path.StartsWithSegments("/api/chunked"))
      {
        throw new InvalidOperationException("Got /api/chunked request on user controller");
      }

      await dispatcher.AddRequest(Request, Response);
    }
  }
}