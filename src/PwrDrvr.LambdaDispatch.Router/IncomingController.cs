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
    await dispatcher.AddRequest(Request, Response);
  }
}