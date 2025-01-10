const http = require('http');
const https = require('https');
const readline = require('readline');
const { URL } = require('url');

// Get URL from command line or use default
const targetUrl = process.argv[2] || 'http://localhost:3000/echo';

try {
  // Parse the URL
  const url = new URL(targetUrl);

  // Determine if we should use http or https
  const client = url.protocol === 'https:' ? https : http;

  // Create readline interface for user input
  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
  });

  // Setup request options
  const options = {
    protocol: url.protocol,
    hostname: url.hostname,
    port: url.port || (url.protocol === 'https:' ? 443 : 80),
    path: url.pathname + url.search,
    method: 'POST',
    headers: {
      'Transfer-Encoding': 'chunked',
      'Content-Type': 'text/plain',
    },
  };

  console.log(`Connecting to ${targetUrl}...`);

  // Create the request
  const req = client.request(options, (res) => {
    console.log(`Connected to server - Status: ${res.statusCode}`);
    console.log('Response headers:', res.headers);
    console.log('\nReceiving data... (Ctrl+C to exit)\n');

    // Handle incoming chunks
    res.on('data', (chunk) => {
      process.stdout.write(`Received: ${chunk.toString()}\n`);
    });

    res.on('end', () => {
      console.log('\nResponse ended');
      process.exit(0);
    });
  });

  // Handle request errors
  req.on('error', (e) => {
    console.error(`Request error: ${e.message}`);
    process.exit(1);
  });

  console.log('Connected to server. Type text and press Enter to send chunks.');
  console.log('Press Ctrl+C to exit\n');

  // Handle user input
  rl.on('line', (input) => {
    if (input) {
      req.write(`${input}\n`);
      console.log(`Sent: ${input}`);
    }
  });

  // Handle Ctrl+C
  process.on('SIGINT', () => {
    console.log('\nClosing connection...');
    req.end();
    process.exit(0);
  });
} catch (error) {
  console.error('Error:', error.message);
  console.log('\nUsage: node client.js [URL]');
  console.log('Example: node client.js http://localhost:3000/echo');
  process.exit(1);
}
