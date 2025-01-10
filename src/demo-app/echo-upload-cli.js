const http = require('http');
const https = require('https');
const fs = require('fs');
const { URL } = require('url');

class SentChunkTracker {
  constructor() {
    this.chunks = []; // [{offset, length, timestamp}]
    this.totalOffset = 0;
  }

  addChunk(length, timestamp) {
    this.chunks.push({
      offset: this.totalOffset,
      length,
      timestamp,
    });
    this.totalOffset += length;
  }

  getLatencyForReceivedBytes(bytesReceived, currentTime) {
    while (this.chunks.length > 0) {
      const chunk = this.chunks[0];
      const chunkEndOffset = chunk.offset + chunk.length;

      // If this chunk has been fully received
      if (bytesReceived >= chunkEndOffset) {
        const latencyNs = Number(currentTime - chunk.timestamp);
        this.chunks.shift(); // Remove processed chunk
        return latencyNs / 1_000_000; // Convert to ms
      }
      break;
    }
    return null;
  }
}

// Get URL and filepath from command line
if (process.argv.length < 4) {
  console.error('Usage: node echo-upload-cli.js <URL> <filepath>');
  console.error('Example: node echo-upload-cli.js http://localhost:3000/echo ./test.bin');
  process.exit(1);
}

const targetUrl = process.argv[2];
const filepath = process.argv[3];

// Stats tracking
let bytesInFlight = 0;
let totalBytesSent = 0;
let totalBytesReceived = 0;
let startTime = null;
const sentChunks = new SentChunkTracker();

function formatBytes(bytes) {
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  if (bytes === 0) return '0 Byte';
  const i = parseInt(Math.floor(Math.log(bytes) / Math.log(1024)));
  return Math.round((bytes / Math.pow(1024, i)) * 100) / 100 + ' ' + sizes[i];
}

function formatDuration(ms) {
  if (!Number.isFinite(ms)) return 'N/A';
  if (ms < 1) return `${(ms * 1000).toFixed(2)}µs`;
  if (ms < 1000) return `${ms.toFixed(2)}ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(2)}s`;
  return `${(ms / 60000).toFixed(2)}m`;
}

try {
  // Parse the URL
  const url = new URL(targetUrl);
  const client = url.protocol === 'https:' ? https : http;

  // Verify file exists and get its size
  const fileSize = fs.statSync(filepath).size;
  console.log(`File size: ${formatBytes(fileSize)}`);

  // Setup request options
  const options = {
    protocol: url.protocol,
    hostname: url.hostname,
    port: url.port || (url.protocol === 'https:' ? 443 : 80),
    path: url.pathname + url.search,
    method: 'POST',
    headers: {
      'Transfer-Encoding': 'chunked',
      'Content-Type': 'application/octet-stream',
    },
  };

  console.log(`Connecting to ${targetUrl}...`);

  // Create the request
  const req = client.request(options, (res) => {
    console.log(`Connected to server - Status: ${res.statusCode}`);

    // Handle incoming chunks
    res.on('data', (chunk) => {
      const receiveTime = process.hrtime.bigint();
      totalBytesReceived += chunk.length;
      bytesInFlight -= chunk.length;

      // Calculate latency based on byte position
      const latencyMs = sentChunks.getLatencyForReceivedBytes(totalBytesReceived, receiveTime);

      const sendProgress = ((totalBytesSent / fileSize) * 100).toFixed(1);
      const receiveProgress = ((totalBytesReceived / fileSize) * 100).toFixed(1);
      const elapsedMs = Number(process.hrtime.bigint() - startTime) / 1_000_000;
      const sendThroughputMBps = totalBytesSent / 1024 / 1024 / (elapsedMs / 1000);
      const receiveThroughputMBps = totalBytesReceived / 1024 / 1024 / (elapsedMs / 1000);

      process.stdout.write(
        `\rSent: ${sendProgress}% (${formatBytes(totalBytesSent)}) @ ${sendThroughputMBps.toFixed(
          1,
        )} MB/s | ` +
          `Received: ${receiveProgress}% (${formatBytes(
            totalBytesReceived,
          )}) @ ${receiveThroughputMBps.toFixed(1)} MB/s | ` +
          `In flight: ${formatBytes(bytesInFlight)} | ` +
          `Chunk latency: ${latencyMs ? formatDuration(latencyMs) : 'N/A'}     `,
      );
    });

    res.on('end', () => {
      const endTime = process.hrtime.bigint();
      const totalTimeMs = Number(endTime - startTime) / 1_000_000;
      const avgThroughputMBps = totalBytesReceived / 1024 / 1024 / (totalTimeMs / 1000);
      console.log('\n\nTransfer complete!');
      console.log(
        `Total bytes: Sent=${formatBytes(totalBytesSent)}, Received=${formatBytes(
          totalBytesReceived,
        )}`,
      );
      console.log(`Total time: ${formatDuration(totalTimeMs)}`);
      console.log(`Average throughput: ${avgThroughputMBps.toFixed(1)} MB/s`);
      if (totalBytesSent !== totalBytesReceived) {
        console.warn('Warning: Bytes sent does not match bytes received!');
      }
      process.exit(0);
    });
  });

  // Handle request errors
  req.on('error', (e) => {
    console.error(`\nRequest error: ${e.message}`);
    process.exit(1);
  });

  // Create read stream with reasonable chunk size
  const readStream = fs.createReadStream(filepath, {
    highWaterMark: 64 * 1024, // 64KB chunks
  });

  startTime = process.hrtime.bigint();

  // Pipe file to request with custom handling
  readStream.on('data', (chunk) => {
    totalBytesSent += chunk.length;
    bytesInFlight += chunk.length;

    // Track when this chunk was sent
    sentChunks.addChunk(chunk.length, process.hrtime.bigint());

    // Check if we need to pause reading
    if (bytesInFlight > 5 * 1024 * 1024) {
      readStream.pause();
      setTimeout(() => readStream.resume(), 100);
    }

    req.write(chunk);
  });

  readStream.on('end', () => {
    const sendTime = Number(process.hrtime.bigint() - startTime) / 1_000_000;
    const sendThroughputMBps = totalBytesSent / 1024 / 1024 / (sendTime / 1000);
    console.log(
      `\nFile send complete: ${formatBytes(totalBytesSent)} sent in ${formatDuration(
        sendTime,
      )} @ ${sendThroughputMBps.toFixed(1)} MB/s`,
    );
    console.log('Waiting for echo response...');
    // Close the request body to signal we're done sending
    req.end();
  });

  readStream.on('error', (err) => {
    console.error(`\nError reading file: ${err.message}`);
    req.end();
    process.exit(1);
  });

  // Handle Ctrl+C
  process.on('SIGINT', () => {
    console.log('\nAborted by user');
    req.end();
    process.exit(0);
  });
} catch (error) {
  console.error('Error:', error.message);
  process.exit(1);
}
