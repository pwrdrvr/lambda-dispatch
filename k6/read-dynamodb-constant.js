import http from "k6/http";

export const options = {
  discardResponseBodies: true,
  scenarios: {
    dispatch: {
      executor: "constant-arrival-rate",
      preAllocatedVUs: 10,
      maxVUs: 100,

      timeUnit: "1s",

      // We want to act like web users clicking to the site (not already on the site)
      // They don't stop clicking on Google links just because we're slow at the moment, they don't know that
      // So they keep arriving at the same rate even if we're hitting a cold start
      rate: 1000,
      duration: "5m",
      exec: "dispatch",
    },
    direct: {
      executor: "constant-arrival-rate",
      preAllocatedVUs: 10,
      maxVUs: 100,

      timeUnit: "1s",

      // We want to act like web users clicking to the site (not already on the site)
      // They don't stop clicking on Google links just because we're slow at the moment, they don't know that
      // So they keep arriving at the same rate even if we're hitting a cold start
      rate: 1000,
      duration: "5m",
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
  // http.get("https://directlambda.ghpublic.pwrdrvr.com/read");
  http.get(
    "https://nwettechi2eak4nspdqlflgiq40olxwi.lambda-url.us-east-2.on.aws/read"
  );
}
