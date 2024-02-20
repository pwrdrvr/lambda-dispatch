using System.Threading.Channels;

namespace PwrDrvr.LambdaDispatch.Router;

public class TokenBucket
{
  private readonly Channel<int> _channel;
  private readonly Timer _timer;

  public TokenBucket(int maxTokens, TimeSpan tokenAddInterval)
  {
    _channel = Channel.CreateBounded<int>(maxTokens);

    // Fill the bucket up
    for (var i = 0; i < maxTokens; i++)
    {
      _channel.Writer.TryWrite(0);
    }

    // Start the timer to add tokens at the specified rate
    _timer = new Timer(_ => AddToken(), null, tokenAddInterval, tokenAddInterval);
  }

  private void AddToken()
  {
    // Try to write to the channel, but don't wait if it's full
    _ = _channel.Writer.TryWrite(0);
  }

  public ValueTask<int> GetTokenAsync(CancellationToken cancellationToken = default)
  {
    // Try to read from the channel, waiting if necessary
    return _channel.Reader.ReadAsync(cancellationToken);
  }

  public bool TryGetToken()
  {
    // Try to read from the channel, but don't wait if it's empty
    return _channel.Reader.TryRead(out var _);
  }
}