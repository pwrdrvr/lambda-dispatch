using System.Runtime.CompilerServices;

public class CombinedStream : Stream
{
  private MemoryStream _bufferStream;
  private Stream _responseStream;
  private bool _bufferStreamRead = false;

  public CombinedStream(MemoryStream bufferStream, Stream responseStream)
  {
    _bufferStream = bufferStream;
    _responseStream = responseStream;
  }

  public override bool CanRead => _bufferStream.CanRead || _responseStream.CanRead;

  public override bool CanSeek => false;

  public override bool CanWrite => false;

  public override long Length => throw new NotSupportedException();

  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

  public override void Flush()
  {
    throw new IOException();
  }

  public override int Read(byte[] buffer, int offset, int count)
  {
    if (_bufferStreamRead)
    {
      return _responseStream.Read(buffer, offset, count);
    }

    int bytesRead = _bufferStream.Read(buffer, offset, count);
    if (bytesRead == 0)
    {
      _bufferStreamRead = true;
      return _responseStream.Read(buffer, offset, count);
    }
    return bytesRead;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
  {
    if (_bufferStreamRead)
    {
      return _responseStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    int bytesRead = _bufferStream.Read(buffer, offset, count);
    if (bytesRead == 0)
    {
      _bufferStreamRead = true;
      return _responseStream.ReadAsync(buffer, offset, count, cancellationToken);
    }
    return Task.FromResult(bytesRead);
  }

  public override long Seek(long offset, SeekOrigin origin)
  {
    throw new NotSupportedException();
  }

  public override void SetLength(long value)
  {
    throw new NotSupportedException();
  }

  public override void Write(byte[] buffer, int offset, int count)
  {
    throw new NotSupportedException();
  }
}