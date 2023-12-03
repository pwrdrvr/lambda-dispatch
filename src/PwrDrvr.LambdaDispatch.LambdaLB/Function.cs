using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using System.Text.Json.Serialization;
using PwrDrvr.LambdaDispatch.Messages;
using System.Net.Cache;

namespace PwrDrvr.LambdaDispatch.LambdaLB;

/// <summary>
/// This class is used to register the input event and return type for the FunctionHandler method with the System.Text.Json source generator.
/// There must be a JsonSerializable attribute for each type used as the input and return type or a runtime error will occur 
/// from the JSON serializer unable to find the serialization information for unknown types.
/// </summary>
[JsonSerializable(typeof(WaiterRequest))]
[JsonSerializable(typeof(WaiterResponse))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext
{
    // By using this partial class derived from JsonSerializerContext, we can generate reflection free JSON Serializer code at compile time
    // which can deserialize our class and properties. However, we must attribute this class to tell it what types to generate serialization code for.
    // See https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-source-generation
}

public class Function
{
    /// <summary>
    /// The main entry point for the Lambda function. The main function is called once during the Lambda init phase. It
    /// initializes the .NET Lambda runtime client passing in the function handler to invoke for each Lambda event and
    /// the JSON serializer to use for converting Lambda JSON format to the .NET types. 
    /// </summary>
    private static async Task Main()
    {
        Func<WaiterRequest, ILambdaContext, Task<WaiterResponse>> handler = FunctionHandler;
        await LambdaBootstrapBuilder.Create(handler, new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>())
            .Build()
            .RunAsync();
    }

    /// <summary>
    /// When using Native AOT extra testing with the deployed Lambda functions is required to ensure
    /// the libraries used in the Lambda function work correctly with Native AOT. If a runtime 
    /// error occurs about missing types or methods the most likely solution will be to remove references to trim-unsafe 
    /// code or configure trimming options. This sample defaults to partial TrimMode because currently the AWS 
    /// SDK for .NET does not support trimming. This will result in a larger executable size, and still does not 
    /// guarantee runtime trimming errors won't be hit. 
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public static async Task<WaiterResponse> FunctionHandler(WaiterRequest request, ILambdaContext context)
    {
        Console.WriteLine($"Received WaiterRequest id: {request.Id}, dispatcherUrl: {request.DispatcherUrl}");

        await Task.Delay(1);

        // Establish a connection back to the control interface
        await using ReverseRequester reverseRequester = new(request.Id, request.DispatcherUrl);

        // Decode received payload
        var aRequest = await reverseRequester.GetRequest();

        Console.WriteLine($"Received request from dispatcher: {aRequest}");

        // TODO: Setup a timeout according to that specified in the payload

        // TODO: Connect back to the specified control interface target in the payload

        // TODO: Use chunked encoding for the request

        // TODO: Dispatch a request if one is received on the reponse channel to the control interface

        // TODO: Send the response back to the control interface on the still-open chunked request channel

        // TODO: If the control interface closes the chunked response without a request, close the request channel

        // TODO: We can send HTTP semantics over the chunked response and request
        // line delimited headers, and a blank line to indicate the end of the headers, then the body

        // We can reuse the sockets too, just closing the request/response bodies not the connection

        var response = new WaiterResponse { Id = request.Id };

        Console.WriteLine($"Responding with WaiterResponse id: {response.Id}");

        return response;
    }
}