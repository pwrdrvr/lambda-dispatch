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
        { target: 10000, duration: "10s" },
        { target: 10000, duration: "10s" },
        { target: 1000, duration: "10s" },
        { target: 1000, duration: "2m" },
        { target: 0, duration: "0" },
      ],
      exec: "dispatch",
    },
  },
};

export function dispatch() {
  http.get("http://127.0.0.1:5001/ping");
}
