//
// This is the entry point when deploying the app as a direct-invoked Lambda fronted by an ALB, API Gateway, or Function URL
//
const { ServerlessAdapter } = require("@h4ad/serverless-adapter");
const {
  ExpressFramework,
} = require("@h4ad/serverless-adapter/lib/frameworks/express");
const {
  DefaultHandler,
} = require("@h4ad/serverless-adapter/lib/handlers/default");
const {
  PromiseResolver,
} = require("@h4ad/serverless-adapter/lib/resolvers/promise");
const { AlbAdapter } = require("@h4ad/serverless-adapter/lib/adapters/aws");
const {
  ApiGatewayV2Adapter,
} = require("@h4ad/serverless-adapter/adapters/aws");
const { app, performInit } = require("./app.cjs");

exports.handler = ServerlessAdapter.new(app)
  .setFramework(new ExpressFramework())
  .setHandler(new DefaultHandler())
  .setResolver(new PromiseResolver())
  .setRespondWithErrors(true)
  .addAdapter(new AlbAdapter())
  .addAdapter(new ApiGatewayV2Adapter())
  .build();

exports.performInit = performInit;
