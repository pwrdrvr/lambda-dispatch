const { DynamoDBClient, PutItemCommand } = require("@aws-sdk/client-dynamodb");

// Create a DynamoDB client
const dbClient = new DynamoDBClient({ region: "us-east-2" }); // replace with your region

// Function to generate a random nested object
function generateRandomObject(depth) {
  const obj = {};
  const keys = Math.floor(Math.random() * 10) + 1; // 1-10 keys
  for (let i = 0; i < keys; i++) {
    const key = `key${i}`;
    if (depth > 0 && Math.random() < 0.5) {
      obj[key] = { M: generateRandomObject(depth - 1) }; // nested map
    } else {
      obj[key] = { S: Math.random().toString(36).substring(2, 15) }; // string
    }
  }
  return obj;
}

async function populateTable() {
  for (let id = 1; id <= 10000; id++) {
    const item = {
      id: { N: id.toString() },
      data: { M: generateRandomObject(Math.floor(Math.random() * 4)) }, // 0-3 levels deep
    };

    // Create a PutItemCommand
    const command = new PutItemCommand({
      TableName: "LambdaDispatchDemo",
      Item: item,
    });

    try {
      // Send the command to DynamoDB
      await dbClient.send(command);
      console.log(`Successfully inserted item with id ${id}`);
    } catch (err) {
      console.error(`Failed to insert item with id ${id}: ${err}`);
    }
  }
}

populateTable();
