import * as cdk from 'aws-cdk-lib';
import { EcrStack } from './ecr-stack';
import { LambdaDispatchStack } from './lambda-dispatch-stack';
import { VpcStack } from './vpc-stack';

// for development, use account/region from cdk cli
const devEnv = {
  account: process.env.CDK_DEFAULT_ACCOUNT,
  region: process.env.CDK_DEFAULT_REGION,
};

const app = new cdk.App();

new EcrStack(app, 'ecr-stack', {
  env: devEnv,
  stackName: 'lambda-dispatch-ecr',
});
const vpcStack = new VpcStack(app, 'vpc-stack', {
  env: devEnv,
  stackName: 'vpc-with-nat-instances',
});
new LambdaDispatchStack(app, 'lambda-dispatch', {
  env: devEnv,
  stackName: 'lambda-dispatch-app-stack',
  vpc: vpcStack.vpc,
});

app.synth();
