import * as cdk from "aws-cdk-lib";
import { EcrStack } from "./ecr-stack";
import { LambdaDispatchStack } from "./lambda-dispatch-stack";
import { VpcStack } from "./vpc-stack";

// for development, use account/region from cdk cli
const devEnv = {
  account: process.env.CDK_DEFAULT_ACCOUNT,
  region: process.env.CDK_DEFAULT_REGION,
};

const app = new cdk.App();

new EcrStack(app, "ecr-stack", { env: devEnv });
new VpcStack(app, "vpc-stack", { env: devEnv });
new LambdaDispatchStack(app, "lambda-dispatch", {
  env: devEnv,
  // If you need to pass any properties from the ECR stack, you can do it here
});

app.synth();
