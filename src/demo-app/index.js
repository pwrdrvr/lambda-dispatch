const express = require("express");
const { DynamoDBClient, GetItemCommand } = require("@aws-sdk/client-dynamodb");
const { promisify } = require("util");

const sleep = promisify(setTimeout);

const app = express();
const port = 3000;

// Create a DynamoDB client
const dbClient = new DynamoDBClient({});

// Start a heartbeat log
// setInterval(() => {
//   console.log(`${new Date().toISOString()} Contained App - Heartbeat`);
// }, 5000);

let firstHealthCheck = true;

app.get("/health", async (req, res) => {
  if (firstHealthCheck) {
    firstHealthCheck = false;
    console.log(
      `${new Date().toISOString()} Contained App - First health check - delayed 8 seconds`
    );
    await sleep(8000);
  }

  res.send("OK");
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
