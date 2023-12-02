using Amazon.Lambda.Core;
using PwrDrvr.LambdaDispatch.Messages;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PwrDrvr.LambdaDispatch.LambdaLB;

public class Function
{

    /// <summary>
    /// Lambda function handler to respond to events coming from an Application Load Balancer.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public WaiterResponse FunctionHandler(WaiterRequest request, ILambdaContext context)
    {
        var response = new WaiterResponse(request.Id);

        // TODO: Establish a connection back to the control interface
        

        // TODO: Decode received payload

        // TODO: Setup a timeout according to that specified in the payload

        // TODO: Connect back to the specified control interface target in the payload

        // TODO: Use chunked encoding for the request

        // TODO: Dispatch a request if one is received on the reponse channel to the control interface

        // TODO: Send the response back to the control interface on the still-open chunked request channel

        // TODO: If the control interface closes the chunked response without a request, close the request channel

        // TODO: We can send HTTP semantics over the chunked response and request
        // line delimited headers, and a blank line to indicate the end of the headers, then the body

        // We can reuse the sockets too, just closing the request/response bodies not the connection

        return response;
    }
}