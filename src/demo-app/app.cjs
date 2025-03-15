import express from 'express';
import { DynamoDBClient, GetItemCommand } from '@aws-sdk/client-dynamodb';
import { S3Client, GetObjectCommand } from '@aws-sdk/client-s3';
import { promisify } from 'util';
import { Transform } from 'stream';
import morgan from 'morgan';
import path from 'path';
import spdy from 'spdy';
import fs from 'fs';
import http2 from 'http2';
import throng from 'throng';

const sleep = promisify(setTimeout);

export const app = express();

const trackBytes = (req, res, next) => {
  // Initialize counters in res.locals
  res.locals.bytesReceived = 0;
  res.locals.bytesSent = 0;

  // Wrap write/end to count response bytes
  const originalWrite = res.write;
  const originalEnd = res.end;

  res.write = function (chunk, ...args) {
    if (chunk) {
      res.locals.bytesSent += chunk.length;
    }
    return originalWrite.call(this, chunk, ...args);
  };

  res.end = function (chunk, ...args) {
    if (chunk) {
      res.locals.bytesSent += chunk.length;
    }
    return originalEnd.call(this, chunk, ...args);
  };

  // Track request bytes
  let receivedBytes = 0;
  req.on('data', (chunk) => {
    receivedBytes += chunk.length;
    res.locals.bytesReceived = receivedBytes;
  });

  next();
};

// Update morgan tokens to use tracked values
morgan.token('request-bytes', (req, res) => {
  return res.locals.bytesReceived ?? 0;
});

morgan.token('response-bytes', (req, res) => {
  return res.locals.bytesSent ?? 0;
});

// Create custom format string
const logFormat =
  ':method :url :status :response-time[3]ms :request-bytes bytes received :response-bytes bytes sent';

// Use the custom format
app.use(trackBytes);
if (process.env.LOGGING === 'true') {
  app.use(morgan(logFormat));
}

const port = 3001;
const spdyPort = 3002;
const spdyInsecurePort = 3003;

// Create clients
const dbClient = new DynamoDBClient({});
const s3Client = new S3Client({});

// Start a heartbeat log
// setInterval(() => {
//   console.log(`${new Date().toISOString()} Contained App - Heartbeat`);
// }, 5000);

let initPerformed = false;

const initSleepMs = parseInt(process.env.INIT_SLEEP_MS) || 7000;

export async function performInit() {
  console.log(
    `${new Date().toISOString()} Contained App - Performing Init - Delaying ${initSleepMs} ms`,
  );
  await sleep(initSleepMs);

  // All the healthchecks should wait until one of them has performed the init
  initPerformed = true;

  console.log(
    `${new Date().toISOString()} Contained App - Performed Init - Delayed ${initSleepMs} ms`,
  );
}

// Serve static files from the "public" directory
app.use('/public', express.static(path.join(__dirname, 'public')));

app.get('/', (req, res) => {
  // HTML for the documentation page
  const html = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Lambda Dispatch Demo App</title>
  <style>
    body {
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, 'Open Sans', 'Helvetica Neue', sans-serif;
      line-height: 1.6;
      color: #333;
      max-width: 900px;
      margin: 0 auto;
      padding: 20px;
    }
    h1, h2, h3 {
      color: #0066cc;
    }
    .endpoint {
      background: #f5f5f5;
      border-left: 4px solid #0066cc;
      padding: 10px 15px;
      margin-bottom: 20px;
      border-radius: 0 4px 4px 0;
    }
    .endpoint h3 {
      margin-top: 0;
    }
    code {
      background: #eee;
      padding: 2px 5px;
      border-radius: 3px;
      font-family: 'SFMono-Regular', Consolas, 'Liberation Mono', Menlo, Courier, monospace;
    }
    pre {
      background: #f8f8f8;
      padding: 10px;
      border-radius: 5px;
      overflow-x: auto;
      border: 1px solid #ddd;
    }
    table {
      border-collapse: collapse;
      width: 100%;
    }
    th, td {
      text-align: left;
      padding: 8px;
      border-bottom: 1px solid #ddd;
    }
    th {
      background-color: #f2f2f2;
    }
  </style>
</head>
<body>
  <h1>Lambda Dispatch Demo App</h1>
  <p>This application demonstrates various features of the Lambda Dispatch system. Use the endpoints below to test different aspects of the service.</p>
  
  <h2>Health and Status Endpoints</h2>
  
  <div class="endpoint">
    <h3>GET /health-quick</h3>
    <p>Quick health check that doesn't wait for initialization.</p>
    <pre><code>curl ${req.protocol}://${req.headers.host}/health-quick</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>GET /health</h3>
    <p>Full health check that ensures initialization is complete (waits for ${initSleepMs}ms).</p>
    <pre><code>curl ${req.protocol}://${req.headers.host}/health</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>GET /ping</h3>
    <p>Simple ping endpoint that returns "pong".</p>
    <pre><code>curl ${req.protocol}://${req.headers.host}/ping</code></pre>
    <p>Load test with hey:</p>
    <pre><code>hey -h2 -c 100 -n 10000 ${req.protocol}://${req.headers.host}/ping</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>GET /headers</h3>
    <p>Returns all HTTP headers from the incoming request as JSON.</p>
    <pre><code>curl ${req.protocol}://${req.headers.host}/headers</code></pre>
  </div>
  
  <h2>Delay and Streaming Endpoints</h2>
  
  <div class="endpoint">
    <h3>GET /delay</h3>
    <p>Delays the response by the specified number of milliseconds.</p>
    <pre><code>curl ${req.protocol}://${req.headers.host}/delay?delay=500</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>GET /chunked-response</h3>
    <p>Returns a chunked response with an initial payload, 5 second delay, then final payload.</p>
    <pre><code>curl ${req.protocol}://${req.headers.host}/chunked-response</code></pre>
  </div>
  
  <h2>Echo Endpoints</h2>
  
  <div class="endpoint">
    <h3>POST /echo</h3>
    <p>Streams the request body directly to the response with back pressure.</p>
    <pre><code>curl -X POST -H "Content-Type: text/plain" --data "Hello World" ${req.protocol}://${req.headers.host}/echo</code></pre>
    <p>Debug mode:</p>
    <pre><code>curl -X POST -H "Content-Type: text/plain" -H "X-Lambda-Dispatch-Debug: true" --data "Hello World" ${req.protocol}://${req.headers.host}/echo</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>POST /echo-slow</h3>
    <p>Reads the entire request body into memory before sending the response.</p>
    <pre><code>curl -X POST -H "Content-Type: text/plain" --data "Hello World" ${req.protocol}://${req.headers.host}/echo-slow</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>POST /double-echo</h3>
    <p>Echoes each chunk of the request body twice, doubling the response size.</p>
    <pre><code>curl -X POST -H "Content-Type: text/plain" --data "Hello World" ${req.protocol}://${req.headers.host}/double-echo</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>POST /half-echo</h3>
    <p>Echoes half of each chunk of the request body, halving the response size.</p>
    <pre><code>curl -X POST -H "Content-Type: text/plain" --data "Hello World" ${req.protocol}://${req.headers.host}/half-echo</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>ALL /debug</h3>
    <p>Returns the request line, headers, and body. Works with any HTTP method.</p>
    <pre><code>curl -X POST -H "Content-Type: text/plain" --data "Hello World" ${req.protocol}://${req.headers.host}/debug</code></pre>
  </div>
  
  <h2>AWS Service Endpoints</h2>
  
  <div class="endpoint">
    <h3>GET /read-s3</h3>
    <p>Reads an image file from S3 and returns it. Good for testing larger payloads.</p>
    <pre><code>curl -o image.jpg ${req.protocol}://${req.headers.host}/read-s3</code></pre>
    <p>Load test with hey:</p>
    <pre><code>hey -h2 -c 100 -n 1000 ${req.protocol}://${req.headers.host}/read-s3</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>GET /read</h3>
    <p>Reads a random item from DynamoDB.</p>
    <pre><code>curl ${req.protocol}://${req.headers.host}/read</code></pre>
    <p>Load test with k6:</p>
    <pre><code>k6 run k6/read-dynamodb-constant.js</code></pre>
  </div>
  
  <div class="endpoint">
    <h3>GET /odd-status</h3>
    <p>Returns an unusual HTTP status code (519).</p>
    <pre><code>curl -i ${req.protocol}://${req.headers.host}/odd-status</code></pre>
  </div>
  
  <h2>Static Files</h2>
  
  <div class="endpoint">
    <h3>GET /public/silly-test-image.jpg</h3>
    <p>Serves a static image file stored in the application.</p>
    <pre><code>curl -o local-image.jpg ${req.protocol}://${req.headers.host}/public/silly-test-image.jpg</code></pre>
  </div>
  
</body>
</html>`;

  res.send(html);
});

app.get('/health-quick', async (req, res) => {
  res.send('OK');
});

app.get('/health', async (req, res) => {
  if (!initPerformed) {
    await performInit();
  }

  res.send('OK');
});

app.get('/ping', async (req, res) => {
  res.send('pong');
});

app.get('/headers', async (req, res) => {
  res.json(req.headers);
});

app.get('/delay', async (req, res) => {
  const delay = parseInt(req.query.delay) || 20;
  await sleep(delay);
  res.send(`Delayed for ${delay} ms`);
});

app.get('/chunked-response', async (req, res) => {
  // Send headers right away
  res.setHeader('Content-Type', 'text/plain');
  res.setHeader('Transfer-Encoding', 'chunked');
  res.status(200);

  // Send initial payload
  res.write('INITIAL PAYLOAD RESPONSE\n');

  // Wait for 5 seconds
  await sleep(5000);

  // Send final payload and close out the response
  res.end('FINAL PAYLOAD RESPONSE\n');
});

// This will read the entire request body into memory before sending the response
app.post('/echo-slow', express.raw({ type: '*/*', limit: '40mb' }), async (req, res) => {
  const contentType = req.get('Content-Type');
  if (contentType) {
    res.set('Content-Type', contentType);
  }
  if (req.body) {
    res.send(req.body);
  } else {
    res.send('');
  }
});

// This will stream the request body to the response
app.post('/echo', async (req, res) => {
  const contentType = req.get('Content-Type');
  const contentLength = req.get('Content-Length');
  const debugMode = req.get('X-Lambda-Dispatch-Debug') === 'true';
  if (contentType) {
    res.set('Content-Type', contentType);
  }
  if (contentLength) {
    res.set('Content-Length', contentLength);
  }

  const logPrefix = `${req.method} ${req.url} HTTP/${req.httpVersion}`;

  const logIt = req.query.log === 'true';

  if (debugMode) {
    // Log the request line
    console.log(`${logPrefix} - STARTING`);
  }

  // Handle client disconnect
  req.on('close', () => {
    console.log('Request closed');
  });

  // Handle potential errors
  req.on('error', (err) => {
    console.log(`${logPrefix} - ERROR`, err);
    if (!res.finished && !res.headersSent) {
      res.status(500).end();
    } else if (!res.finished) {
      res.destroy();
    }
  });

  let totalBytesReceived = 0;

  const logger = new Transform({
    transform(chunk, encoding, callback) {
      const timestamp = new Date().toISOString();
      totalBytesReceived += chunk.length;
      if (debugMode) {
        console.log(
          `${logPrefix} - RECEIVED chunk ${chunk.length} bytes, total ${totalBytesReceived} bytes at ${timestamp}`,
        );
      }

      this.push(chunk);

      callback();
    },
    flush(callback) {
      if (debugMode) {
        console.log(`${logPrefix} - FINISHED`);
      }
      callback();
    },
  });

  // Pipe the req body to the response with back pressure
  // This will stream incoming bytes as they arrive
  if (logIt) {
    req = req.pipe(logger);
  }
  req
    .pipe(res)
    .on('error', (err) => {
      console.error(`${logPrefix} - PIPE ERROR`, err);
      if (!res.finished) {
        res.status(500).end();
      }
    })
    .on('finish', () => {
      if (debugMode) {
        console.log(`${logPrefix} - RESPONSE FINISHED`);
      }
      if (!res.finished) {
        res.end();
      }
    });
});

app.post('/double-echo', async (req, res) => {
  const contentType = req.get('Content-Type');
  const contentLength = req.get('Content-Length');

  if (contentType) {
    res.set('Content-Type', contentType);
  }
  if (contentLength) {
    // Double the content length since we're duplicating each chunk
    res.set('Content-Length', (parseInt(contentLength) * 2).toString());
  }

  // Handle client disconnect
  req.on('close', () => {
    console.log('Request closed');
  });

  // Handle potential errors
  req.on('error', (err) => {
    console.error('Request error:', err);
    if (!res.finished && !res.headersSent) {
      res.status(500).end();
    } else if (!res.finished) {
      res.destroy();
    }
  });

  // Create transform stream that doubles each chunk
  const doubler = new Transform({
    transform(chunk, encoding, callback) {
      // Push the chunk twice
      this.push(chunk);
      this.push(chunk);
      callback();
    },
  });

  // Pipe through doubler transform then to response
  req
    .pipe(doubler)
    .pipe(res)
    .on('error', (err) => {
      console.error('Pipe error:', err);
      if (!res.finished) {
        res.status(500).end();
      }
    })
    .on('finish', () => {
      if (!res.finished) {
        res.end();
      }
    });
});

app.post('/half-echo', async (req, res) => {
  const contentType = req.get('Content-Type');

  if (contentType) {
    res.set('Content-Type', contentType);
  }

  // Handle client disconnect
  req.on('close', () => {
    console.log('Request closed');
  });

  // Handle potential errors
  req.on('error', (err) => {
    console.error('Request error:', err);
    if (!res.finished && !res.headersSent) {
      res.status(500).end();
    } else if (!res.finished) {
      res.destroy();
    }
  });

  // Create transform stream that halves each chunk
  const halver = new Transform({
    transform(chunk, encoding, callback) {
      // Only push half of the chunk
      const halfLength = chunk.length >> 1;
      this.push(chunk.slice(0, halfLength), 'binary');
      callback();
    },
  });

  // Pipe through halver transform then to response
  req
    .pipe(halver)
    .pipe(res)
    .on('error', (err) => {
      console.error('Pipe error:', err);
      if (!res.finished) {
        res.status(500).end();
      }
    })
    .on('finish', () => {
      if (!res.finished) {
        res.end();
      }
    });
});

app.all('/debug', async (req, res) => {
  // Set response to plain text
  res.setHeader('Content-Type', 'text/plain');

  // Write the request line and headers first
  const headerDump = [
    `${req.method} ${req.url} HTTP/${req.httpVersion}`,
    ...Object.entries(req.headers).map(([key, value]) => `${key}: ${value}`),
    '\n',
  ].join('\n');

  // When no body just send the headers
  if (!req.headers['content-length'] && !/chunked/.test(req.headers['transfer-encoding'])) {
    res.send(headerDump);
    return;
  }

  // Otherwise write headers and pipe body
  res.write(headerDump);
  req.pipe(res);
});

app.get('/read-s3', async (req, res) => {
  // Create a GetObjectCommand
  const command = new GetObjectCommand({
    Bucket: 'pwrdrvr-lambdadispatch-demo',
    Key: 'silly-test-image.jpg',
  });

  try {
    // Send the command to S3
    const data = await s3Client.send(command);

    if (data.ContentLength) {
      res.setHeader('Content-Length', data.ContentLength);
    }
    if (data.ContentType) {
      res.setHeader('Content-Type', data.ContentType);
    }
    res.setHeader('Content-Disposition', 'attachment; filename=silly-test-image.jpg');
    // Pipe the S3 Object content to the response
    data.Body.pipe(res).on('error', (err) => {
      console.error(`${new Date().toISOString()} Contained App - Failed to read item`, err);
      res.status(500).send(err.toString());
    });
  } catch (err) {
    console.error(`${new Date().toISOString()} Contained App - Failed to read item`, err);
    res.status(500).send(err.toString());
  }
});

app.get('/odd-status', async (req, res) => {
  res.status(519).send("I'm a teapot");
});

app.get('/read', async (req, res) => {
  // Log that we got a request
  // console.log(`${new Date().toISOString()} Contained App - Received request`);
  // Generate a random id in the range 1-10000
  const id = Math.floor(Math.random() * 10000) + 1;

  // Create a GetItemCommand
  const command = new GetItemCommand({
    TableName: 'LambdaDispatchDemo',
    Key: {
      id: { N: id.toString() },
    },
  });

  try {
    // Send the command to DynamoDB
    const data = await dbClient.send(command);
    // console.log(
    //   `${new Date().toISOString()} Contained App - Success`,
    //   data.Item
    // );

    // Pause for 50 ms to simulate calling an upstream that takes longer
    // await sleep(50);

    res.json(data.Item);
  } catch (err) {
    console.error(`${new Date().toISOString()} Contained App - Failed to read item`, err);
    res.status(500).send(err.toString());
  }
});

if (process.env.NUMBER_OF_WORKERS) {
  throng({
    workers: process.env.NUMBER_OF_WORKERS ?? '1',
    grace: 60000,
    master: async () => {
      console.log(`> Master started - starting workers`);
    },
    worker: async (id) => {
      console.log(`> Worker ${id} started`);

      createServer();

      console.log(`> Worker ${id} - listening`);
    },
    signals: ['SIGTERM', 'SIGINT'],
  });
} else {
  createServer();
}

function createServer() {
  app.listen(port, () => {
    console.log(`App listening at http://localhost:${port}`);
  });

  const certPath = '../../certs/lambdadispatch.local.crt';
  const keyPath = '../../certs/lambdadispatch.local.key';

  if (fs.existsSync(certPath) && fs.existsSync(keyPath)) {
    const options = {
      key: fs.readFileSync(keyPath),
      cert: fs.readFileSync(certPath),
    };

    const server = spdy.createServer({ ...options }, app);

    server.listen(spdyPort, () => {
      console.log(`App listening on HTTP2 at https://localhost:${spdyPort}`);
    });

    const serverInsecure = http2.createSecureServer({ ...options }, (req, res) => {
      res.writeHead(200, { 'Content-Type': req.headers['content-type'] });
      res.write('\r\n');
      req.on('data', (chunk) => {
        // Print each body chunk as hex and possibly UTF-8 text
        console.log(
          `${new Date().toISOString()} Contained App - Received chunk: ${chunk.toString('hex')}`,
        );
        res.write(chunk);
      });
      req.on('aborted', (err) => {
        console.log(`${new Date().toISOString()} Contained App - Request aborted`, err);
      });
      req.on('end', () => {
        res.end();
      });
    });

    serverInsecure.listen(spdyInsecurePort, () => {
      console.log(`App listening on HTTP2 at http://localhost:${spdyInsecurePort}`);
    });
  } else {
    console.log('Certificate or key file not found. HTTP/2 server not started.');
  }
}
