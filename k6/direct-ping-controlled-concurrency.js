import http from "k6/http";

export const options = {
  discardResponseBodies: true,
  scenarios: {
    direct: {
      executor: "ramping-vus",
      startVUs: 500,
      stages: [
        { target: 1, duration: "0" },
        { target: 1, duration: "10s" },
        { target: 500, duration: "0" },
        { target: 500, duration: "5m" },
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
export function direct() {
  http.get("https://directlambda.ghpublic.pwrdrvr.com/ping");
}
