import express from "express";
import { DynamoDBClient, GetItemCommand } from "@aws-sdk/client-dynamodb";
import { promisify } from "util";
import path from "path";

const sleep = promisify(setTimeout);

export const app = express();
const port = 3000;

// Create a DynamoDB client
const dbClient = new DynamoDBClient({});

// Start a heartbeat log
// setInterval(() => {
//   console.log(`${new Date().toISOString()} Contained App - Heartbeat`);
// }, 5000);

let initPerformed = false;

export async function performInit() {
  initPerformed = true;
  debugger;
  console.log(
    `${new Date().toISOString()} Contained App - Performing Init - Delaying 8 seconds`
  );
  await sleep(8000);
  console.log(
    `${new Date().toISOString()} Contained App - Performed Init - Delayed 8 seconds`
  );
}

// Serve static files from the "public" directory
app.use("/public", express.static(path.join(__dirname, "public")));

app.get("/health", async (req, res) => {
  if (!initPerformed) {
    performInit();
  }

  res.send("OK");
});

app.post(
  "/echo",
  express.raw({ type: "*/*", limit: "40mb" }),
  async (req, res) => {
    const contentType = req.get("Content-Type");
    res.set("Content-Type", contentType);
    res.send(req.body);
  }
);

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
