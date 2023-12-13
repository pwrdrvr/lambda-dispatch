using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PwrDrvr.LambdaDispatch.LambdaLB;

public class HttpReverseRequester
{
  private readonly ILogger<HttpReverseRequester> _logger = LoggerInstance.CreateLogger<HttpReverseRequester>();

  private readonly string _id;
  private readonly string _dispatcherUrl;

  private readonly Uri _uri;

  private readonly HttpClient _client;

  private readonly HttpClientHandler _handler;

  public HttpReverseRequester(string id, string dispatcherUrl)
  {
    _id = id;
    _dispatcherUrl = dispatcherUrl;

    // Change Proto and Port
    _uri = new UriBuilder(_dispatcherUrl)
    {
      Port = 5003,
      Scheme = "https",
    }.Uri;

    _handler = new HttpClientHandler();
    _handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
    {
      // If the certificate is a valid, signed certificate, return true.
      if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
      {
        return true;
      }

      // If it's a self-signed certificate for the specific host, return true.
      // TODO: Get the CN name to allow
      if (cert != null && cert.Subject.Contains("CN=lambdadispatch.local"))
      {
        return true;
      }

      // In all other cases, return false.
      return false;
    };

    _client = new HttpClient(_handler)
    {
      DefaultRequestVersion = new Version(2, 0),
      Timeout = TimeSpan.FromMinutes(15),
    };
  }

  public ValueTask DisposeAsync()
  {
    _client.Dispose();
    _handler.Dispose();

    return ValueTask.CompletedTask;
  }

  /// <summary>
  /// Get the request from the response of the Router
  /// </summary>
  /// <returns>
  /// outer status code, requestToRun, requestForResponse
  /// </returns>
  public async Task<(int, HttpRequestMessage, HttpRequestMessage, Stream, HttpDuplexContent)> GetRequest()
  {
    var duplexContent = new HttpDuplexContent();

    var request = new HttpRequestMessage(HttpMethod.Post, _uri)
    {
      Version = new Version(2, 0)
    };
    request.Headers.Host = "lambdadispatch.local:5003";
    request.Headers.Add("X-Lambda-Id", _id);
    request.Content = duplexContent;

    var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    // Get the stream that we can write the response to
    Stream requestStreamForResponse = await duplexContent.WaitForStreamAsync();

    // HH: This is from the dotnet example, but they do not await the SendAsync that
    // returns when the response headers are read. I don't think we need this
    // since we await it.
    // Flush the content stream. Otherwise, the request headers are not guaranteed to be sent.
    // await requestStreamForResponse.FlushAsync();

    if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
    {
      return ((int)response.StatusCode, null!, null!, null!, null!);
    }

    //
    // Read the actual request off the Response from the router
    //
    var receivedRequest = new HttpRequestMessage();

    using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true))
    {
      // Read the request line
      var firstLine = await reader.ReadLineAsync();
      if (firstLine == null)
      {
        throw new EndOfStreamException("End of stream reached while reading request line");
      }
      var partsOfFirstLine = firstLine.Split(' ');
      receivedRequest.Method = new HttpMethod(partsOfFirstLine[0]);
      receivedRequest.RequestUri = new Uri(partsOfFirstLine[1], UriKind.Relative);
      receivedRequest.Version = new Version(partsOfFirstLine[2].Split('/')[1]);

      // Read the headers
      string? requestHeaderLine;
      var contentHeaders = new List<(string, string)>();
      while (!string.IsNullOrEmpty(requestHeaderLine = await reader.ReadLineAsync()))
      {
        // Split the header into key and value
        var parts = requestHeaderLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
        var key = parts[0];
        var value = parts[1];

        if (string.Compare(key, "Content-Type", StringComparison.OrdinalIgnoreCase) == 0)
        {
          contentHeaders.Add((key, value));
          // The Host header is not allowed to be set by the client
          // DotNet will throw `System.InvalidOperationException` if you try to set it
        }
        else
        {
          receivedRequest.Headers.Add(key, value);
        }
      }

      // Set the request body
      // TODO: The StreamReader will have stolen and buffered some of the underlying stream data
      receivedRequest.Content = new StreamContent(reader.BaseStream);

      // Add all the content headers
      foreach (var (key, value) in contentHeaders)
      {
        receivedRequest.Content.Headers.Add(key, value);
      }
    }

    return ((int)response.StatusCode, receivedRequest, request, requestStreamForResponse, duplexContent);
  }

  /// <summary>
  /// Send the response on the request to the Router
  /// </summary>
  /// <returns></returns>
  public async Task SendResponse(HttpResponseMessage response, HttpRequestMessage requestForResponse, Stream requestStreamForResponse, HttpDuplexContent duplexContent)
  {
    // Write the status line
    await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/{requestForResponse.Version} {(int)response.StatusCode} {response.ReasonPhrase}\r\n"));
    // Copy the headers
    foreach (var header in response.Headers)
    {
      await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes($"{header.Key}: {header.Value}\r\n"));
    }
    await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes("X-Lambda-Id: " + _id + "\r\n"));
    await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes("Server: PwrDrvr.LambdaDispatch.LambdaLB\r\n"));
    await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));

    // Copy the body from the request to the response
    await response.Content.CopyToAsync(requestStreamForResponse);
    // await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes("Hello World!\r\n"));
    await requestStreamForResponse.FlushAsync();
    requestStreamForResponse.Close();
    duplexContent.Complete();
    requestForResponse.Dispose();
  }
}