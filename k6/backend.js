import http from "k6/http";

export const options = {
  discardResponseBodies: true,
  scenarios: {
    dispatch: {
      executor: "ramping-arrival-rate",
      preAllocatedVUs: 150,
      maxVUs: 600,
      startRate: 300,
      timeUnit: "1m",
      exec: "dispatch",
      stages: [
        { target: 120, duration: "10s" },
        { target: 100000, duration: "2m" },
        { target: 1000, duration: "5m" },
        { target: 200000, duration: "2m" },
        { target: 1000, duration: "5m" },
        { target: 300000, duration: "2m" },
        { target: 0, duration: "0" },
      ],
    },
    direct: {
      executor: "ramping-arrival-rate",
      preAllocatedVUs: 150,
      maxVUs: 300,
      startRate: 300,
      timeUnit: "1m",
      exec: "direct",
      stages: [
        { target: 120, duration: "10s" },
        { target: 100000, duration: "2m" },
        { target: 1000, duration: "5m" },
        { target: 200000, duration: "2m" },
        { target: 1000, duration: "5m" },
        { target: 300000, duration: "2m" },
        { target: 0, duration: "0" },
      ],
    },
  },
};

export function dispatch() {
  http.get("https://lambdadispatch.ghpublic.pwrdrvr.com/read");
}
export function direct() {
  http.get("https://directlambda.ghpublic.pwrdrvr.com/read");
}
