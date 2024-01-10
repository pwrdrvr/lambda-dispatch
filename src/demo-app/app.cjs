import express from "express";
import { DynamoDBClient, GetItemCommand } from "@aws-sdk/client-dynamodb";
import { S3Client, GetObjectCommand } from "@aws-sdk/client-s3";
import { promisify } from "util";
import path from "path";
import spdy from "spdy";
import fs from "fs";
import http2 from "http2";

const sleep = promisify(setTimeout);

export const app = express();
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

export async function performInit() {
  console.log(
    `${new Date().toISOString()} Contained App - Performing Init - Delaying 7 seconds`
  );
  await sleep(7000);

  // All the healthchecks should wait until one of them has performed the init
  initPerformed = true;

  console.log(
    `${new Date().toISOString()} Contained App - Performed Init - Delayed 7 seconds`
  );
}

// Serve static files from the "public" directory
app.use("/public", express.static(path.join(__dirname, "public")));

app.get("/health", async (req, res) => {
  if (!initPerformed) {
    await performInit();
  }

  res.send("OK");
});

app.get("/ping", async (req, res) => {
  res.send("pong");
});

app.get("/delay", async (req, res) => {
  const delay = req.query.delay || 20;
  await sleep(delay);
  res.send(`Delayed for ${delay} ms`);
});

app.get("/chunked-response", async (req, res) => {
  // Send headers right away
  res.setHeader("Content-Type", "text/plain");
  res.setHeader("Transfer-Encoding", "chunked");
  res.status(200);

  // Send initial payload
  res.write("INITIAL PAYLOAD RESPONSE\n");

  // Wait for 5 seconds
  await sleep(5000);

  // Send final payload and close out the response
  res.end("FINAL PAYLOAD RESPONSE\n");
});

app.post(
  "/echo",
  express.raw({ type: "*/*", limit: "40mb" }),
  async (req, res) => {
    const contentType = req.get("Content-Type");
    if (contentType) {
      res.set("Content-Type", contentType);
    }
    if (req.body) {
      res.send(req.body);
    } else {
      res.send("");
    }
  }
);

app.get("/read-s3", async (req, res) => {
  // Create a GetObjectCommand
  const command = new GetObjectCommand({
    Bucket: "pwrdrvr-lambdadispatch-demo",
    Key: "silly-test-image.jpg",
  });

  try {
    // Send the command to S3
    const data = await s3Client.send(command);

    if (data.ContentLength) {
      res.setHeader("Content-Length", data.ContentLength);
    }
    if (data.ContentType) {
      res.setHeader("Content-Type", data.ContentType);
    }
    res.setHeader(
      "Content-Disposition",
      "attachment; filename=silly-test-image.jpg"
    );
    // Pipe the S3 Object content to the response
    data.Body.pipe(res).on("error", (err) => {
      console.error(
        `${new Date().toISOString()} Contained App - Failed to read item`,
        err
      );
      res.status(500).send(err.toString());
    });
  } catch (err) {
    console.error(
      `${new Date().toISOString()} Contained App - Failed to read item`,
      err
    );
    res.status(500).send(err.toString());
  }
});

app.get("/read", async (req, res) => {
  // Log that we got a request
  // console.log(`${new Date().toISOString()} Contained App - Received request`);
  // Generate a random id in the range 1-10000
  const id = Math.floor(Math.random() * 10000) + 1;

  // Create a GetItemCommand
  const command = new GetItemCommand({
    TableName: "LambdaDispatchDemo",
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
    await sleep(50);

    res.json(data.Item);
  } catch (err) {
    console.error(
      `${new Date().toISOString()} Contained App - Failed to read item`,
      err
    );
    res.status(500).send(err.toString());
  }
});

app.listen(port, () => {
  console.log(`App listening at http://localhost:${port}`);
});

const certPath = "../../certs/lambdadispatch.local.crt";
const keyPath = "../../certs/lambdadispatch.local.key";

if (fs.existsSync(certPath) && fs.existsSync(keyPath)) {
  const options = {
    key: fs.readFileSync(keyPath),
    cert: fs.readFileSync(certPath),
  };

  const server = spdy.createServer({ ...options }, app);

  server.listen(spdyPort, () => {
    console.log(`App listening on HTTP2 at https://localhost:${spdyPort}`);
  });

  const serverInsecure = http2.createSecureServer(
    { ...options },
    (req, res) => {
      res.writeHead(200, { "Content-Type": req.headers["content-type"] });
      res.write("\r\n");
      req.on("data", (chunk) => {
        // Print each body chunk as hex and possibly UTF-8 text
        console.log(
          `${new Date().toISOString()} Contained App - Received chunk: ${chunk.toString(
            "hex"
          )}`
        );
        res.write(chunk);
      });
      req.on("end", () => {
        res.end();
      });
    }
  );

  serverInsecure.listen(spdyInsecurePort, () => {
    console.log(
      `App listening on HTTP2 at http://localhost:${spdyInsecurePort}`
    );
  });
} else {
  console.log("Certificate or key file not found. HTTP/2 server not started.");
}
