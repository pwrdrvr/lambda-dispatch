import http from "k6/http";

export const options = {
  discardResponseBodies: true,
  scenarios: {
    dispatch: {
      executor: "ramping-vus",
      preAllocatedVUs: 150,
      maxVUs: 600,
      startVUs: 1,
      stages: [
        { target: 1, duration: "0" },
        { target: 1, duration: "10s" },
        // https://k6.io/docs/examples/instant-load-increase/
        { target: 10, duration: "0" },
        { target: 10, duration: "2m" },
        { target: 20, duration: "0" },
        { target: 20, duration: "2m" },
        { target: 100, duration: "0" },
        { target: 100, duration: "2m" },
        { target: 200, duration: "0" },
        { target: 200, duration: "2m" },
        { target: 0, duration: "0" },
      ],
      exec: "dispatch",
    },
    direct: {
      executor: "ramping-vus",
      preAllocatedVUs: 150,
      maxVUs: 600,
      startVUs: 1,
      stages: [
        { target: 1, duration: "0" },
        { target: 1, duration: "10s" },
        // https://k6.io/docs/examples/instant-load-increase/
        { target: 10, duration: "0" },
        { target: 10, duration: "2m" },
        { target: 20, duration: "0" },
        { target: 20, duration: "2m" },
        { target: 100, duration: "0" },
        { target: 100, duration: "2m" },
        { target: 200, duration: "0" },
        { target: 200, duration: "2m" },
        { target: 0, duration: "0" },
      ],
      exec: "direct",
    },
  },
};

//
// Respond immediately with `pong`
// This allows cleanly testing the cold starts that happen when concurrent
// requests suddenly increase
//
export function dispatch() {
  http.get("https://lambdadispatch.ghpublic.pwrdrvr.com/ping");
}
export function direct() {
  http.get("https://directlambda.ghpublic.pwrdrvr.com/ping");
}
