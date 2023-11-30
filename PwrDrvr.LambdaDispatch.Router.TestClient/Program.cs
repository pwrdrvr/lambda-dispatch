using System.Net.Http;

namespace PwrDrvr.LambdaDispatch.Router.TestClient;

public class Program
{
  static public async Task TestChunkedRequest()
  {
    using var client = new HttpClient();
    client.BaseAddress = new Uri("http://localhost:5001");

    // Prepare the request
    var request = new HttpRequestMessage(HttpMethod.Post, "/api/chunked");

    // Set transfer encoding to chunked
    request.Headers.TransferEncodingChunked = true;


    // Set the request body to a PushStreamContent that sends a chunked request body
    request.Content = new StreamRequestContent("Chunked request");

    // Send the request
    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);


    // new StreamContent(async (stream, content, context) =>
    // {
    //   using (var writer = new StreamWriter(stream))
    //   {
    //     for (int i = 0; i < 10; i++)
    //     {
    //       await writer.WriteAsync($"Chunk {i}\n");
    //       await writer.FlushAsync();

    //       // Sleep for 1 second between chunks
    //       await Task.Delay(TimeSpan.FromSeconds(1));
    //     }
    //   }
    // });


    // Print the response body to the console
    var responseBody = await response.Content.ReadAsStringAsync();
    Console.WriteLine(responseBody);
  }

  static async Task Main(string[] args)
  {
    await TestChunkedRequest();
  }
}