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
app.use(morgan(logFormat));

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

  // Create transform stream that doubles each chunk
  const logger = new Transform({
    transform(chunk, encoding, callback) {
      const timestamp = new Date().toISOString();
      totalBytesReceived += chunk.length;
      if (debugMode) {
        console.log(`${logPrefix} - RECEIVED chunk ${chunk.length} bytes, total ${totalBytesReceived} bytes at ${timestamp}`);
      }

      this.push(chunk);

      callback();
    },
    flush(callback) {
      if (debugMode) {
        console.log(`${logPrefix} - FINISHED`);
      }
      callback();
    }
  });

  // Pipe the req body to the response with back pressure
  // This will stream incoming bytes as they arrive
  req
    .pipe(logger)
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
    }
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
