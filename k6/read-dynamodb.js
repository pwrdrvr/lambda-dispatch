import http from "k6/http";

export const options = {
  discardResponseBodies: true,
  scenarios: {
    dispatch: {
      executor: "ramping-arrival-rate",
      preAllocatedVUs: 150,
      maxVUs: 600,
      startRate: 300,
      timeUnit: "1s",
      stages: [
        { target: 1, duration: "0" },
        { target: 1, duration: "20s" },
        { target: 10, duration: "0" },
        { target: 10, duration: "20s" },
        { target: 1000, duration: "0" },
        { target: 1000, duration: "30s" },
        { target: 2000, duration: "0" },
        { target: 2000, duration: "30s" },
        { target: 3000, duration: "0" },
        { target: 3000, duration: "30s" },
        { target: 0, duration: "0" },
      ],
      exec: "dispatch",
    },
    direct: {
      executor: "ramping-arrival-rate",
      preAllocatedVUs: 150,
      maxVUs: 600,
      startRate: 300,
      timeUnit: "1s",
      stages: [
        { target: 1, duration: "0" },
        { target: 1, duration: "20s" },
        { target: 10, duration: "0" },
        { target: 10, duration: "20s" },
        { target: 1000, duration: "0" },
        { target: 1000, duration: "30s" },
        { target: 2000, duration: "0" },
        { target: 2000, duration: "30s" },
        { target: 3000, duration: "0" },
        { target: 3000, duration: "30s" },
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
