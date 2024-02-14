import http from "k6/http";

export const options = {
  discardResponseBodies: true,
  scenarios: {
    direct: {
      executor: "constant-arrival-rate",
      preAllocatedVUs: 300,
      maxVUs: 1000,

      timeUnit: "1s",

      // We want to act like web users clicking to the site (not already on the site)
      // They don't stop clicking on Google links just because we're slow at the moment, they don't know that
      // So they keep arriving at the same rate even if we're hitting a cold start
      rate: 200,
      duration: "5m",
      exec: "direct",
    },
  },
};

export function direct() {
  http.get("https://directlambda.ghpublic.pwrdrvr.com/ping");
}
