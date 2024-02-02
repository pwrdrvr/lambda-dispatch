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
        { target: 10, duration: "0" },
        { target: 10, duration: "2m" },
        { target: 100, duration: "0" },
        { target: 100, duration: "5m" },
        { target: 200, duration: "0" },
        { target: 200, duration: "5m" },
        { target: 100, duration: "0" },
        { target: 100, duration: "5m" },
        { target: 200, duration: "0" },
        { target: 200, duration: "5m" },
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
        { target: 10, duration: "0" },
        { target: 10, duration: "2m" },
        { target: 100, duration: "0" },
        { target: 100, duration: "5m" },
        { target: 200, duration: "0" },
        { target: 200, duration: "5m" },
        { target: 100, duration: "0" },
        { target: 100, duration: "5m" },
        { target: 200, duration: "0" },
        { target: 200, duration: "5m" },
        { target: 0, duration: "0" },
      ],
      exec: "direct",
    },
  },
};

//
// Read 164 KB JPEG (binary) off of S3 and return it in the response
// This is a near worst case for direct lambda because it must base64 encode the response
//
export function dispatch() {
  http.get("https://lambdadispatch.ghpublic.pwrdrvr.com/read-s3");
}
export function direct() {
  http.get("https://directlambda.ghpublic.pwrdrvr.com/read-s3");
}
