using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using System.Text.Json.Serialization;
using PwrDrvr.LambdaDispatch.Messages;
using Microsoft.Extensions.Logging;
using AWS.Logger;
using System.Net;
using System.Net.Sockets;

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
    private static readonly ILogger _logger;

    static Function()
    {
        _logger = LoggerInstance.CreateLogger<Function>();
    }

    /// <summary>
    /// The main entry point for the Lambda function. The main function is called once during the Lambda init phase. It
    /// initializes the .NET Lambda runtime client passing in the function handler to invoke for each Lambda event and
    /// the JSON serializer to use for converting Lambda JSON format to the .NET types. 
    /// </summary>
    private static async Task Main()
    {
        _logger.LogInformation("Lambda Started");
#if NATIVE_AOT
        Task.Run(MetricsRegistry.PrintMetrics);
#endif
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
        try
        {
            // Reset the metrics
#if NATIVE_AOT
            MetricsRegistry.Metrics.Manage.Reset();
#endif

            var lastWakeupTime = DateTime.Now;

            _logger.LogDebug("Thread pool size: {ThreadCount}", ThreadPool.ThreadCount);

            // Log if we got a late arriving Invoke packet (delayed before us)
            if ((DateTime.Now - request.SentTime).TotalSeconds > 1)
            {
                _logger.LogWarning("Request took {Seconds} seconds to get here", (DateTime.Now - request.SentTime).TotalSeconds);
            }
            _logger.LogInformation("Received WaiterRequest id: {Id}, dispatcherUrl: {DispatcherUrl}", request.Id, request.DispatcherUrl);

            using var lambdaIdScope = _logger.BeginScope("LambdaId: {LambdaId}", request.Id);

            // TODO: We can potentially make this static or a map on dispatcherUrl to requester
            // Each request repeats the Lambda ID and we do not need to re-establish sockets
            // if the DispatcherUrl has not actually changed
            await using var reverseRequester = new HttpReverseRequester(request.Id, request.DispatcherUrl);

            var NumberOfChannels = request.NumberOfChannels;

            if (ThreadPool.ThreadCount < NumberOfChannels)
            {
                _logger.LogDebug("Increasing thread pool size to {NumberOfChannels}", NumberOfChannels);
                ThreadPool.SetMinThreads(NumberOfChannels * 2, NumberOfChannels * 2);
            }

            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            List<Task> tasks = new List<Task>();
            for (int i = 0; i < NumberOfChannels; i++)
            {
                // Required to get a unique variable that identifies the task
                int taskNumber = i;
                tasks.Add(Task.Run(async () =>
                {
                    using var taskNumberScope = _logger.BeginScope("TaskNumber: {TaskNumber}", taskNumber);

#if NATIVE_AOT
                    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.ChannelsOpen);
#endif

                    try
                    {
                        _logger.LogInformation("Starting task");
                        while (!token.IsCancellationRequested)
                        {
                            var channelId = Guid.NewGuid().ToString();
                            using var channelIdScope = _logger.BeginScope("ChannelId: {ChannelId}", channelId);

                            lastWakeupTime = DateTime.Now;

#if NATIVE_AOT
                            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LastWakeupTime, () => (DateTime.Now - lastWakeupTime).TotalMilliseconds);
#endif

                            try
                            {
#if NATIVE_AOT
                                using var timer = MetricsRegistry.Metrics.Measure.Timer.Time(MetricsRegistry.IncomingRequestTimer);
#endif

                                _logger.LogDebug("Getting request from Router");

                                (var outerStatus, var receivedRequest, var requestForResponse, var requestStreamForResponse, var duplexContent)
                                    = await reverseRequester.GetRequest(channelId);

                                _logger.LogDebug("Got request from Router");

                                // The OuterStatus is the status returned by the Router on it's Response
                                // This is NOT the status of the Lambda function's Response
                                if (outerStatus == (int)HttpStatusCode.Conflict)
                                {
#if NATIVE_AOT
                                    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RequestConflictCount);
#endif

                                    // Stop the other tasks from looping
                                    cts.Cancel();
                                    _logger.LogInformation("Router told us to close our connection and not re-open it");
                                    return;
                                }
                                else if (outerStatus != (int)HttpStatusCode.OK)
                                {
#if NATIVE_AOT
                                    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RequestConflictCount);
#endif

                                    // Stop the other tasks from looping
                                    cts.Cancel();
                                    _logger.LogInformation("Router gave a non-200 status code, stopping");
                                    return;
                                }

#if NATIVE_AOT
                                MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RequestCount);
#endif

                                // Read the bytes off the request body, if any
                                // TODO: This is not always a string
                                var requestBody = await receivedRequest.Content.ReadAsStringAsync();

                                var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                                {
                                    Content = new StringContent("Hello From LambdaLB!\r\n"),
                                };

                                await reverseRequester.SendResponse(response, requestForResponse, requestStreamForResponse, duplexContent, channelId);

#if NATIVE_AOT
                                MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RespondedCount);
#endif

                                _logger.LogDebug("Sent response to Router");
                            }
                            catch (EndOfStreamException ex)
                            {
                                _logger.LogError(ex, "End of stream exception caught in task");
                                // We do not cancel, we just loop around and make a new request
                                // If the new request gets a 409 status, then we will stop the loop
                            }
                            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.NameResolutionError)
                            {
                                if (!cts.IsCancellationRequested)
                                {
                                    // Connection was refused, log at a lower level
                                    _logger.LogWarning("Name resolution error");

                                    cts.Cancel();
                                }
                            }
                            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
                            {
                                if (!cts.IsCancellationRequested)
                                {
                                    // Connection was refused, log at a lower level
                                    _logger.LogWarning("Connection refused");

                                    cts.Cancel();
                                }
                            }
                            // HttpRequestException is up to the point the response headers are read
                            // Docs: https://devblogs.microsoft.com/dotnet/dotnet-8-networking-improvements/
                            catch (HttpRequestException ex)
                            {
                                if (!cts.IsCancellationRequested)
                                {
                                    // Some other kind of HttpRequestException, log as error
                                    _logger.LogError(ex, "HttpRequestException caught");

                                    // If the address is invalid or connections are being terminated then we stop
                                    cts.Cancel();
                                }
                            }
                            catch (HttpIOException ex) when (ex.HttpRequestError == HttpRequestError.InvalidResponse)
                            {
                                if (!cts.IsCancellationRequested)
                                {
                                    // Connection was refused, log at a lower level
                                    _logger.LogError(ex, "Invalid response");

                                    cts.Cancel();
                                }
                            }
                            catch (HttpIOException ex)
                            {
                                if (!cts.IsCancellationRequested)
                                {
                                    _logger.LogError(ex, "HttpIOException caught");

                                    // If the address is invalid or connections are being terminated then we stop
                                    cts.Cancel();
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // This is expected
                            }
                            catch (Exception ex)
                            {
                                if (!cts.IsCancellationRequested)
                                {
                                    _logger.LogError(ex, "Exception caught in task");

                                    // TODO: Should we stop?
                                    cts.Cancel();
                                }
                            }
                        }

                        _logger.LogInformation("Exiting task");
                    }
                    finally
                    {
#if NATIVE_AOT
                        MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.ChannelsOpen);
                        MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.ChannelsClosed);
#endif
                    }
                }));

            }

            // Add a task that pings the Router every 5 seconds
            // Needs it's own cancellation token
            var pingCts = new CancellationTokenSource();
            var pinger = Task.Run(async () =>
            {
                while (!pingCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), pingCts.Token);
                        await reverseRequester.Ping();
                    }
                    catch (TaskCanceledException)
                    {
                        // This is expected
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Exception caught in ping task");
                    }
                }
            });

            // TODO: Setup a timeout according to that specified in the payload
            // Note: the code below is only going to work cleanly under constant load
#if true || !DEBUG
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(45));
            var finishedTask = await Task.WhenAny(Task.WhenAll(tasks), timeoutTask);

            // This just prevents looping around again
            cts.Cancel();

            // Ask the Router to close our instance and connections when we timed out
            if (finishedTask == timeoutTask)
            {
                await reverseRequester.CloseInstance();
            }
#endif
            await Task.WhenAll(tasks);

            // Tell the pinger to exit
            pingCts.Cancel();
            await pinger;

            var response = new WaiterResponse { Id = request.Id };

            _logger.LogInformation("Responding with WaiterResponse id: {Id}", response.Id);

            return response;
        }
        finally
        {
            // Dump the metrics one last time
#if NATIVE_AOT
            await Task.WhenAll(MetricsRegistry.Metrics.ReportRunner.RunAllAsync());
#endif
        }
    }
}