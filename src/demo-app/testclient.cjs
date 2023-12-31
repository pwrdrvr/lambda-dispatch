// Make requests to https://localhost:3001/ping in a tight loop over http2
// Every 5 seconds, print the total number of requests completed
const spdy = require("spdy");
const https = require("https");

const spdyPort = 3001;
let totalRequests = 0;

async function main() {
  setInterval(() => {
    console.log(`${new Date().toISOString()} Total Requests: ${totalRequests}`);
  }, 5000);

  while (true) {
    await makeRequests();
  }
}

async function makeRequests() {
  const resp = await fetch("https://localhost:3001/ping");
  totalRequests++;
}

main();
