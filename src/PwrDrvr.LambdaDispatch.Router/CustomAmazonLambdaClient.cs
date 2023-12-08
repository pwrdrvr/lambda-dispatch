using Amazon.Lambda;
using Amazon.Lambda.Model;
using System.Text;
using System.Text.Json;

public class CustomAmazonLambdaClient : AmazonLambdaClient
{
  private readonly HttpClient httpClient = new HttpClient();

  public CustomAmazonLambdaClient() : base()
  {
  }

  public CustomAmazonLambdaClient(AmazonLambdaConfig config) : base(config)
  {
  }

  public override async Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken = default)
  {
    // Create a new HttpRequestMessage
    var httpRequest = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5050/runtime/test-event");

    // Serialize the request object to JSON and add it to the HttpRequestMessage
    var json = JsonSerializer.Serialize(request);
    httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

    // Send the request
    var response = await this.httpClient.SendAsync(httpRequest, cancellationToken);

    // Deserialize the response
    var responseContent = await response.Content.ReadAsStringAsync();
    var invokeResponse = JsonSerializer.Deserialize<InvokeResponse>(responseContent);

    return invokeResponse;
  }
}