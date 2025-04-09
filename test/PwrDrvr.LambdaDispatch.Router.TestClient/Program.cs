using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Buffers;
using System.Net.Security;

namespace PwrDrvr.LambdaDispatch.Router.TestClient;

public class Program
{
  private const int CHUNK_SIZE = 128 * 1024; // 64KB chunks

  private static bool verbose = false;

  // Helper method for conditional logging
  private static void VerboseLog(string message)
  {
    if (verbose)
    {
      Console.WriteLine(message);
    }
  }

  private static string FormatBytes(long bytes)
  {
    string[] sizes = { "B", "KB", "MB", "GB" };
    int order = 0;
    double size = bytes;
    while (size >= 1024 && order < sizes.Length - 1)
    {
      order++;
      size /= 1024;
    }
    return $"{size:0.##} {sizes[order]}";
  }

  static async Task Main(string[] args)
  {
    if (args.Length < 2)
    {
      Console.Error.WriteLine("Usage: program <url> <filepath> [--verbose]");
      Console.Error.WriteLine("Example: program http://foo.bar.com/route?query=false myfile.dat --verbose");
      return;
    }

    verbose = args.Contains("--verbose");

    var url = new Uri(args[0]);
    var filepath = args[1];

    // Use path + query in request line, default to / if empty
    var pathAndQuery = string.IsNullOrEmpty(url.PathAndQuery) ? "/" : url.PathAndQuery;
    var host = url.Host;
    var port = url.Port != -1 ? url.Port : 80;

    var fileInfo = new FileInfo(filepath);
    if (!fileInfo.Exists)
    {
      Console.Error.WriteLine($"File not found: {filepath}");
      return;
    }

    Console.WriteLine($"Uploading {FormatBytes(fileInfo.Length)} to {host}:{port}");

    using var client = new TcpClient();
    await client.ConnectAsync(host, port);
    client.NoDelay = true;

    Stream stream;
    if (url.Scheme.ToLower() == "https")
    {
      var sslStream = new SslStream(
          client.GetStream(),
          false
      );

      await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
      {
        TargetHost = host,
        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
      });

      stream = sslStream;
    }
    else
    {
      stream = client.GetStream();
    }

    // Convert headers to bytes and send
    var headers = $"POST {pathAndQuery} HTTP/1.1\r\n" +
                 $"Host: {host}:{port}\r\n" +
                 "Transfer-Encoding: chunked\r\n" +
                 "Content-Type: application/octet-stream\r\n" +
                 "X-Lambda-Dispatch-Debug: true\r\n" +
                 "\r\n";
    var headerBytes = Encoding.ASCII.GetBytes(headers);
    await stream.WriteAsync(headerBytes);

    var stopwatch = Stopwatch.StartNew();
    var totalBytesSent = 0L;
    var lastLogTime = stopwatch.ElapsedMilliseconds;

    // Start reading response in parallel
#if false
    var readTask = ReadResponseBusted(stream);
#else
    var readTask = ReadResponse(stream);
#endif

    // Send file in chunks
    using (var fileStream = File.OpenRead(filepath))
    {
      var buffer = new byte[CHUNK_SIZE];
      int bytesRead;

      try
      {
        while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
        {
          // Write chunk size header
          var sizeHeader = $"{bytesRead:X}\r\n";
          var sizeBytes = Encoding.ASCII.GetBytes(sizeHeader);
          await stream.WriteAsync(sizeBytes);

          // Write chunk data
          await stream.WriteAsync(buffer.AsMemory(0, bytesRead));

          // Write the trailing \r\n after the chunk data
          await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
          // await stream.FlushAsync();

          // await Task.Delay(30);

          totalBytesSent += bytesRead;

          var now = stopwatch.ElapsedMilliseconds + 1;
          VerboseLog($"Sent: {FormatBytes(totalBytesSent)} " +
                  $"Rate: {FormatBytes(totalBytesSent * 1000 / now)}/s");
        }
      }
      catch (Exception ex)
      {
        Console.WriteLine($"Error: {ex.Message}");
        throw;
      }

      Console.WriteLine("File sent, sending final chunk");
    }

    // Send final chunk
    await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
    await stream.FlushAsync();

    // Remember that HTTP/1.1 sockets are allowed to stay open and accept another request
    // So closing this request completely just requires ending the chunked encoding
    // correctly

    // Wait for all data to be received
    var totalBytesReceived = await readTask;

    stopwatch.Stop();
    var duration = stopwatch.ElapsedMilliseconds / 1000.0;

    Console.WriteLine("\nTransfer complete!");
    Console.WriteLine($"Sent: {FormatBytes(totalBytesSent)}, {totalBytesSent} bytes");
    Console.WriteLine($"Received: {FormatBytes(totalBytesReceived)}. {totalBytesReceived} bytes");
    Console.WriteLine($"Duration: {duration:F1}s");
    Console.WriteLine($"Average Upload Rate: {FormatBytes((long)(totalBytesSent / duration))}/s");
    Console.WriteLine($"Average Download Rate: {FormatBytes((long)(totalBytesReceived / duration))}/s");
  }

  public static async Task<long> ReadResponseBusted(NetworkStream stream)
  {
    var headerBuffer = new byte[16 * 1024];
    var responseBuilder = new StringBuilder();
    var headerComplete = false;
    var totalBytesReceived = 0L;

    // Read response headers
    while (!headerComplete)
    {
      var bytesRead = await stream.ReadAsync(headerBuffer);
      var text = Encoding.ASCII.GetString(headerBuffer, 0, bytesRead);
      responseBuilder.Append(text);

      var response = responseBuilder.ToString();
      var headerEnd = response.IndexOf("\r\n\r\n");
      if (headerEnd >= 0)
      {
        // Print headers
        var headers = response.Substring(0, headerEnd).Split("\r\n");
        foreach (var header in headers)
        {
          Console.WriteLine($"< {header}");
        }
        headerComplete = true;

        // TODO: Count any left-over data in the buffer as part of the response body size
        var bodyStart = headerEnd + 4;
        var leftOver = response.Length - bodyStart;
        if (leftOver > 0)
        {
          Console.WriteLine($"< {leftOver} bytes left over in buffer");
          totalBytesReceived += leftOver;
        }
      }
    }

    var stopwatch = Stopwatch.StartNew();

    var buffer = new byte[CHUNK_SIZE];
    while (true)
    {
      var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
      totalBytesReceived += bytesRead;

      if (bytesRead == 0)
      {
        break;
      }

      var now = stopwatch.ElapsedMilliseconds + 1;
      VerboseLog($"Received: {FormatBytes(totalBytesReceived)} " +
              $"Rate: {FormatBytes(totalBytesReceived * 1000 / now)}/s");
    }

    return totalBytesReceived;
  }

  /// <summary>
  /// Get the response
  /// </summary>
  /// <returns>
  /// outer status code, requestToRun, requestForResponse
  /// </returns>
  public static async Task<long> ReadResponse(Stream stream)
  {
    //
    // Read the actual Response from the router
    //
    // TODO: Get the 32 KB header size limit from configuration
    var headerBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
    try
    {
      // Read up to max headers size of data
      // Read until we fill the bufer OR we get an EOF
      int totalBytesRead = 0;
      int idxToExamine = 0;
      int idxPriorLineFeed = int.MinValue;
      int idxHeadersLast = int.MinValue;
      int startOfNextLine = 0;
      while (true)
      {
        if (totalBytesRead >= headerBuffer.Length)
        {
          // Buffer is full
          break;
        }

        var bytesRead = await stream.ReadAsync(headerBuffer, totalBytesRead, headerBuffer.Length - totalBytesRead);
        if (bytesRead == 0)
        {
          // Done reading
          break;
        }

        totalBytesRead += bytesRead;

        // Check if we have a `\r\n\r\n` sequence
        // We have to check for this in the buffer because we can't
        // read past the end of the stream
        for (int i = idxToExamine; i < totalBytesRead; i++)
        {
          // If this is a `\n` and the -1 or -2 character is `\n` then we we are done
          if (headerBuffer[i] == (byte)'\n' && (idxPriorLineFeed == i - 1 || (idxPriorLineFeed == i - 2 && headerBuffer[i - 1] == (byte)'\r')))
          {
            // We found the `\r\n\r\n` sequence
            // We are done reading
            idxHeadersLast = i;
            break;
          }
          else if (headerBuffer[i] == (byte)'\n')
          {
            // Update the last line feed index
            idxPriorLineFeed = i;
          }
        }

        if (idxHeadersLast != int.MinValue)
        {
          // We found the `\r\n\r\n` sequence
          // We are done reading
          break;
        }
      }

      //
      // NOTE: This starts reading the buffer again at the start
      // This could be combined with the end of headers check above to read only once
      //

      // Read the status line
      int endOfStatusLine = Array.IndexOf(headerBuffer, (byte)'\n', 0, totalBytesRead);
      if (endOfStatusLine == -1)
      {
        // Handle error: '\n' not found in the buffer
        throw new Exception("Status line not found in response");
      }

      string firstLine = Encoding.UTF8.GetString(headerBuffer, 0, endOfStatusLine);

      if (string.IsNullOrEmpty(firstLine))
      {
        // We need to let go of the request body
        throw new EndOfStreamException("End of stream reached while reading request line");
      }

      var partsOfFirstLine = firstLine.Split(' ');
      if (partsOfFirstLine.Length < 2)
      {
        throw new Exception($"Invalid response line: {firstLine}");
      }
      Console.WriteLine($"< {firstLine}");

      // Start processing the rest of the headers from the character after '\n'
      startOfNextLine = endOfStatusLine + 1;
      var contentHeaders = new List<(string, string)>();

      // Process the rest of the headers
      var hasBody = false;
      long contentLengthValue = -1;
      bool isChunked = false;
      while (startOfNextLine < totalBytesRead)
      {
        // Find the index of the next '\n' in headerBuffer
        int endOfLine = Array.IndexOf(headerBuffer, (byte)'\n', startOfNextLine, totalBytesRead - startOfNextLine);
        if (endOfLine == -1)
        {
          // No more '\n' found
          break;
        }

        // Check if this is the end of the headers
        if (endOfLine == startOfNextLine || (endOfLine == startOfNextLine + 1 && headerBuffer[startOfNextLine] == '\r'))
        {
          // End of headers
          // Move the start to the character after '\n'
          startOfNextLine = endOfLine + 1;
          break;
        }

        // We don't want the \n or the possibly proceeding \r
        var endOfHeaderIdx = endOfLine;
        if (headerBuffer[endOfHeaderIdx - 1] == '\r')
        {
          endOfHeaderIdx--;
        }

        // Extract the line
        string headerLine = Encoding.UTF8.GetString(headerBuffer, startOfNextLine, endOfHeaderIdx - startOfNextLine);

        Console.WriteLine($"< {headerLine}");

        // Parse the line as a header
        var parts = headerLine.Split(new[] { ": " }, 2, StringSplitOptions.None);

        var key = parts[0];
        // Join all the parts after the first one
        var value = string.Join(", ", parts.Skip(1));
        if (string.Compare(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) == 0)
        {
          hasBody = true;
          isChunked = true;
        }
        else if (string.Compare(key, "Content-Type", StringComparison.OrdinalIgnoreCase) == 0)
        {
          contentHeaders.Add((key, value));
        }
        else if (string.Compare(key, "Content-Length", StringComparison.OrdinalIgnoreCase) == 0)
        {
          contentLengthValue = long.Parse(value);
          hasBody = true;
        }

        // Move the start to the character after '\n'
        startOfNextLine = endOfLine + 1;
      }

      // Flush any remaining bytes in the buffer
      MemoryStream accumulatedBuffer = new MemoryStream();
      if (startOfNextLine < totalBytesRead)
      {
        // Write the bytes after the headers to the memory stream
        accumulatedBuffer.Write(headerBuffer.AsSpan(startOfNextLine, totalBytesRead - startOfNextLine));
        accumulatedBuffer.Flush();
        accumulatedBuffer.Position = 0;
      }

      // Make a combined stream that returns the buffer first then the stream
      var combinedStream = new CombinedStream(accumulatedBuffer, stream);

      // Set the request body, if there is one
      if (hasBody)
      {
        var stopwatch = Stopwatch.StartNew();
        long totalBytesReceived = 0;
        var buffer = new byte[CHUNK_SIZE];

        if (isChunked)
        {
          while (true)
          {
            // Read chunk size line
            var chunkSizeBytes = new List<byte>();
            int b;
            while ((b = combinedStream.ReadByte()) != -1)
            {
              if (b == '\n') break;
              if (b != '\r') chunkSizeBytes.Add((byte)b);
            }

            if (chunkSizeBytes.Count == 0)
            {
              if (b == -1)
              {
                Console.WriteLine("ERROR - Received (chunked): EOF when reading chunk size");
              }
              else
              {
                Console.WriteLine("ERROR - Received (chunked): empty chunk size");
              }
              break;
            }

            var chunkSizeHex = Encoding.ASCII.GetString(chunkSizeBytes.ToArray());
            var chunkSize = Convert.ToInt32(chunkSizeHex, 16);
            if (chunkSize == 0)
            {
              VerboseLog($"Received (chunked): end chunk");
              // Read trailing CRLF
              await combinedStream.ReadAsync(new byte[2]);
              break;
            }

            // Read chunk data
            var remaining = chunkSize;
            while (remaining > 0)
            {
              var toRead = Math.Min(remaining, buffer.Length);
              int bytesRead = await combinedStream.ReadAsync(buffer.AsMemory(0, toRead));

              totalBytesReceived += bytesRead;

              if (bytesRead <= 0)
              {
                Console.WriteLine("ERROR - Received (chunked): EOF when reading chunk data");
                break;
              }
              remaining -= bytesRead;
            }

            // Read trailing CRLF after the data
            await combinedStream.ReadAsync(new byte[2]);

            var now = stopwatch.ElapsedMilliseconds + 1;
            VerboseLog($"Received (chunked): {FormatBytes(totalBytesReceived)} " +
                    $"Rate: {FormatBytes(totalBytesReceived * 1000 / now)}/s");
          }

          Console.WriteLine("File received (chunked)");

          return totalBytesReceived;
        }
        else
        {
          var leftToRead = contentLengthValue;
          while (leftToRead > 0)
          {
            int bytesRead = await combinedStream.ReadAsync(buffer.AsMemory(0, buffer.Length));

            leftToRead -= bytesRead;
            totalBytesReceived += bytesRead;

            if (bytesRead == 0)
            {
              break;
            }

            var now = stopwatch.ElapsedMilliseconds + 1;
            VerboseLog($"Received: {FormatBytes(totalBytesReceived)} " +
                    $"Rate: {FormatBytes(totalBytesReceived * 1000 / now)}/s");
          }

          Console.WriteLine("File received");

          return totalBytesReceived;
        }
      }

      return 0;
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(headerBuffer);
    }
  }
}
