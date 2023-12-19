const express = require("express");
const { DynamoDBClient, GetItemCommand } = require("@aws-sdk/client-dynamodb");

const app = express();
const port = 3000;

// Create a DynamoDB client
const dbClient = new DynamoDBClient({ region: "us-east-2" }); // replace with your region

app.get("/health", (req, res) => {
  res.send("OK");
});

app.get("/read", async (req, res) => {
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
    // console.log("Success", data.Item);
    res.json(data.Item);
  } catch (err) {
    console.error(err);
    res.status(500).send(err.toString());
  }
});

app.listen(port, () => {
  console.log(`App listening at http://localhost:${port}`);
});
