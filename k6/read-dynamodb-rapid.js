import http from "k6/http";

export const options = {
  discardResponseBodies: true,
  scenarios: {
    dispatch: {
      executor: "ramping-vus",
      startVUs: 1,
      stages: [
        { target: 1, duration: "0" },
        { target: 1, duration: "10s" },
        // https://k6.io/docs/examples/instant-load-increase/
        { target: 10, duration: "0" },
        { target: 10, duration: "20s" },
        { target: 20, duration: "0" },
        { target: 20, duration: "20s" },
        { target: 50, duration: "0" },
        { target: 50, duration: "20s" },
        { target: 100, duration: "0" },
        { target: 100, duration: "20s" },
        { target: 200, duration: "0" },
        { target: 200, duration: "20s" },
        { target: 300, duration: "0" },
        { target: 300, duration: "20s" },
        { target: 400, duration: "0" },
        { target: 400, duration: "20s" },
        { target: 500, duration: "0" },
        { target: 500, duration: "20s" },
        { target: 600, duration: "0" },
        { target: 600, duration: "20s" },
        { target: 0, duration: "0" },
      ],
      exec: "dispatch",
    },
    direct: {
      executor: "ramping-vus",
      startVUs: 1,
      stages: [
        { target: 1, duration: "0" },
        { target: 1, duration: "10s" },
        // https://k6.io/docs/examples/instant-load-increase/
        { target: 10, duration: "0" },
        { target: 10, duration: "20s" },
        { target: 20, duration: "0" },
        { target: 20, duration: "20s" },
        { target: 50, duration: "0" },
        { target: 50, duration: "20s" },
        { target: 100, duration: "0" },
        { target: 100, duration: "20s" },
        { target: 200, duration: "0" },
        { target: 200, duration: "20s" },
        { target: 300, duration: "0" },
        { target: 300, duration: "20s" },
        { target: 400, duration: "0" },
        { target: 400, duration: "20s" },
        { target: 500, duration: "0" },
        { target: 500, duration: "20s" },
        { target: 600, duration: "0" },
        { target: 600, duration: "20s" },
        { target: 0, duration: "0" },
      ],
      exec: "direct",
    },
  },
};

//
// Reads a random ~1.5 KB record out of DynamoDB
// This is very fast and a very small response size
// This is essentially the worst case scenario for lambda dispatch
// This also contains a 50 ms sleep in the `/read` handler to simulate calling an upstream that takes longer
// This mean that the CPU on Lambda Dispatch will be underutilized unless
// the number of concurrent requests is quite high
//
export function dispatch() {
  http.get("https://lambdadispatch.ghpublic.pwrdrvr.com/read");
}
export function direct() {
  http.get("https://directlambda.ghpublic.pwrdrvr.com/read");
}
