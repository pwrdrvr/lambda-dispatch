using System.Net.Sockets;
using System.Diagnostics;
using System.Text;

namespace PwrDrvr.LambdaDispatch.Router.TestClient;

public class Program
{
  private const int CHUNK_SIZE = 64 * 1024; // 64KB chunks

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
      Console.Error.WriteLine("Usage: program <url> <filepath>");
      Console.Error.WriteLine("Example: program http://foo.bar.com/route?query=false myfile.dat");
      return;
    }

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

    using var stream = client.GetStream();

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
    var totalBytesReceived = 0L;
    var lastLogTime = stopwatch.ElapsedMilliseconds;

    // Start reading response in parallel
    var readTask = Task.Run(async () =>
    {
      var headerBuffer = new byte[16 * 1024];
      var responseBuilder = new StringBuilder();
      var headerComplete = false;

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

      var buffer = new byte[CHUNK_SIZE];
      while (true)
      {
        // // Read chunk size line
        // var chunkSizeBytes = new List<byte>();
        // int b;
        // while ((b = stream.ReadByte()) != -1)
        // {
        //   if (b == '\n') break;
        //   if (b != '\r') chunkSizeBytes.Add((byte)b);
        // }

        // if (chunkSizeBytes.Count == 0) break;

        // var chunkSizeHex = Encoding.ASCII.GetString(chunkSizeBytes.ToArray());
        // var chunkSize = Convert.ToInt32(chunkSizeHex, 16);
        // if (chunkSize == 0) break;

        // // Read chunk data
        // var remaining = chunkSize;
        // while (remaining > 0)
        // {
        // var toRead = Math.Min(remaining, buffer.Length);
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
        totalBytesReceived += bytesRead;

        if (bytesRead == 0)
        {
          break;
        }
        // remaining -= bytesRead;
        // }

        // Read trailing CRLF
        // await stream.ReadAsync(new byte[2]);

        var now = stopwatch.ElapsedMilliseconds + 1;
        Console.WriteLine($"Received: {FormatBytes(totalBytesReceived)} " +
                $"Rate: {FormatBytes(totalBytesReceived * 1000 / now)}/s");
      }
    });

    // Send file in chunks
    using (var fileStream = File.OpenRead(filepath))
    {
      var buffer = new byte[CHUNK_SIZE];
      int bytesRead;

      while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
      {
        // Write chunk size header
        var sizeHeader = $"{bytesRead:X}\r\n";
        var sizeBytes = Encoding.ASCII.GetBytes(sizeHeader);
        await stream.WriteAsync(sizeBytes);

        // Write chunk data
        await stream.WriteAsync(buffer.AsMemory(0, bytesRead));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));

        totalBytesSent += bytesRead;

        var now = stopwatch.ElapsedMilliseconds + 1;
        Console.WriteLine($"Sent: {FormatBytes(totalBytesSent)} " +
                $"Rate: {FormatBytes(totalBytesSent * 1000 / now)}/s");
      }

      Console.WriteLine("File sent, sending final chunk");
    }

    // Send final chunk
    await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
    await stream.FlushAsync();
    //  Close the write side of the stream
    client.Client.Shutdown(SocketShutdown.Send);

    // Wait for all data to be received
    await readTask;

    stopwatch.Stop();
    var duration = stopwatch.ElapsedMilliseconds / 1000.0;

    Console.WriteLine("\nTransfer complete!");
    Console.WriteLine($"Sent: {FormatBytes(totalBytesSent)}");
    Console.WriteLine($"Received: {FormatBytes(totalBytesReceived)}");
    Console.WriteLine($"Duration: {duration:F1}s");
    Console.WriteLine($"Average Upload Rate: {FormatBytes((long)(totalBytesSent / duration))}/s");
    Console.WriteLine($"Average Download Rate: {FormatBytes((long)(totalBytesReceived / duration))}/s");
  }
}