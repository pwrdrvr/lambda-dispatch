namespace PwrDrvr.LambdaDispatch.Router.TestClient;

using System.Net;
using System.Net.Http;

public class StreamRequestContent : HttpContent
{
  private readonly string _data;
  public StreamRequestContent(string data)
  {
    _data = data;
  }

  // Docs:
  // https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcontent?view=net-8.0

  // Minimal implementation needed for an HTTP request content,
  // i.e. a content that will be sent via HttpClient, contains the 2 following methods.
  protected override bool TryComputeLength(out long length)
  {
    // This content doesn't support pre-computed length and
    // the request will NOT contain Content-Length header.
    length = 0;
    return false;
  }

  // SerializeToStream* methods are internally used by CopyTo* methods
  // which in turn are used to copy the content to the NetworkStream.
  protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
  {
    using var writer = new StreamWriter(stream);
    for (int i = 0; i < 10; i++)
    {
      await writer.WriteAsync($"Chunk {i}\n");
      await writer.FlushAsync();

      // Sleep for 1 second between chunks
      await Task.Delay(TimeSpan.FromSeconds(1));
    }
  }
}