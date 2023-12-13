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

  public IncomingController(Dispatcher dispatcher)
  {
    this.dispatcher = dispatcher;
  }

  [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")]
  public async Task HandleRequest()
  {
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