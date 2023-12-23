//
// This is the entry point when deploying the app as a direct-invoked Lambda fronted by an ALB, API Gateway, or Function URL
//
const serverlessExpress = require("@codegenie/serverless-express");
const { app, performInit } = require("./app.cjs");
exports.handler = serverlessExpress({ app });
exports.performInit = performInit;
