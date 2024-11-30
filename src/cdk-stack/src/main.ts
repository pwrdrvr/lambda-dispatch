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

// Only create VPC stack and use its VPC for the main app stack
const vpcStack = new VpcStack(app, 'vpc-stack', {
  env: devEnv,
  stackName: 'vpc-with-nat-instances',
});
const lambdaDispatchStack = new LambdaDispatchStack(app, 'lambda-dispatch', {
  env: devEnv,
  stackName: 'lambda-dispatch-app-stack',
  vpc: vpcStack.vpc,
  loadBalancerArn: cdk.Fn.importValue('vpc-with-nat-instances-LoadBalancerArn'),
  httpsListenerArn: cdk.Fn.importValue('vpc-with-nat-instances-HttpsListenerArn'),
  loadBalancerSecurityGroupId: cdk.Fn.importValue(
    'vpc-with-nat-instances-LoadBalancerSecurityGroupId',
  ),
  loadBalancerDnsName: cdk.Fn.importValue('vpc-with-nat-instances-ALBDnsName'),
  loadBalancerHostedZoneId: cdk.Fn.importValue('vpc-with-nat-instances-ALBCanonicalHostedZoneId'),
  ecsClusterArn: cdk.Fn.importValue('vpc-with-nat-instances-ClusterArn'),
  ecsClusterName: cdk.Fn.importValue('vpc-with-nat-instances-ClusterName'),
});
cdk.Tags.of(lambdaDispatchStack).add('Name', 'lambda-dispatch');

const lambdaDispatchStackPr = new LambdaDispatchStack(app, 'lambda-dispatch-pr', {
  env: devEnv,
  stackName: `lambda-dispatch-app-stack-pr-${process.env.PR_NUMBER}`,
  vpc: vpcStack.vpc,
  loadBalancerArn: cdk.Fn.importValue('vpc-with-nat-instances-LoadBalancerArn'),
  httpsListenerArn: cdk.Fn.importValue('vpc-with-nat-instances-HttpsListenerArn'),
  loadBalancerSecurityGroupId: cdk.Fn.importValue(
    'vpc-with-nat-instances-LoadBalancerSecurityGroupId',
  ),
  loadBalancerDnsName: cdk.Fn.importValue('vpc-with-nat-instances-ALBDnsName'),
  loadBalancerHostedZoneId: cdk.Fn.importValue('vpc-with-nat-instances-ALBCanonicalHostedZoneId'),
  ecsClusterArn: cdk.Fn.importValue('vpc-with-nat-instances-ClusterArn'),
  ecsClusterName: cdk.Fn.importValue('vpc-with-nat-instances-ClusterName'),
  removalPolicy: cdk.RemovalPolicy.DESTROY,
});
cdk.Tags.of(lambdaDispatchStackPr).add('Name', `lambda-dispatch-pr-${process.env.PR_NUMBER}`);

app.synth();
