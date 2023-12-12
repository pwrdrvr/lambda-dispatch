using System.Net.Http;

namespace PwrDrvr.LambdaDispatch.LambdaLB;

public class HttpReverseRequester : IReverseRequester
{
  private readonly string _id;
  private readonly string _dispatcherUrl;

  private readonly Uri _uri;

  private readonly HttpClient _client;

  private readonly Stream _stream;

  public HttpReverseRequester(string id, string dispatcherUrl)
  {
    _id = id;
    _dispatcherUrl = dispatcherUrl;

    // Parse out the host, port, and path
    _uri = new Uri(_dispatcherUrl);

    _client = new HttpClient();

    _stream = new MemoryStream();
  }

  public ValueTask DisposeAsync()
  {
    throw new NotImplementedException();
  }

  public Task<int> GetRequest()
  {
    var request = new HttpRequestMessage(HttpMethod.Post, _uri);
    request.Content = new StreamContent(_stream);

    throw new NotImplementedException();
  }

  public Task SendResponse()
  {
    throw new NotImplementedException();
  }
}