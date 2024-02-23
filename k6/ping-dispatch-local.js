import http from "k6/http";

export const options = {
  discardResponseBodies: true,
  scenarios: {
    dispatch: {
      executor: "ramping-arrival-rate",
      preAllocatedVUs: 10,
      maxVUs: 1000,
      startRate: 20,
      timeUnit: "1s",
      stages: [
        // { target: 1, duration: "10s" },
        { target: 10, duration: "0s" },
        { target: 100, duration: "1s" },
        { target: 1000, duration: "1s" },
        { target: 1000, duration: "1s" },
        { target: 10000, duration: "60s" },
        { target: 10000, duration: "5m" },
        // { target: 2000, duration: "60s" },
        // { target: 2000, duration: "5m" },
        // { target: 3000, duration: "60s" },
        // { target: 3000, duration: "5m" },
        { target: 0, duration: "0" },
      ],
      exec: "dispatch",
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
  http.get("http://127.0.0.1:5001/ping");
}
