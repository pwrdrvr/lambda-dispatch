using System.Net.Http;
using System.IO.Pipelines;

namespace PwrDrvr.LambdaDispatch.Router.TestClient;

public class Program
{
  static public async Task TestChunkedRequest()
  {
    using (var client = new HttpClient())
    {
      client.BaseAddress = new Uri("http://localhost:5001");

      // Prepare the request
      var request = new HttpRequestMessage(HttpMethod.Post, "/api/chunked");

      // Create a pipe to send the request body in chunks
      var pipe = new Pipe();
      request.Content = new StreamContent(pipe.Reader.AsStream());

      // Start sending the request
      var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

      // Write the request body in chunks
      await using (var writer = new StreamWriter(pipe.Writer.AsStream()))
      {
        for (int i = 0; i < 10; i++)
        {
          await writer.WriteAsync($"Chunk {i}\n");
          await writer.FlushAsync();

          // Sleep for 1 second between chunks
          await Task.Delay(TimeSpan.FromSeconds(1));
        }
      }

      // Wait for the response
      var response = await responseTask;

      // Print the response body to the console
      var responseBody = await response.Content.ReadAsStringAsync();
      Console.WriteLine(responseBody);
    }
  }
  static async Task Main(string[] args)
  {
    await TestChunkedRequest();
  }
}