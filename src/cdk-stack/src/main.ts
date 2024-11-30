import * as cdk from 'aws-cdk-lib';
import { EcrStack } from './ecr-stack';
import { EcsClusterStack } from './ecs-cluster-stack';
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
  stackName: 'lambda-dispatch-vpc',
});
new EcsClusterStack(app, 'ecs-stack', {
  env: devEnv,
  stackName: 'lambda-dispatch-ecs',
  vpc: vpcStack.vpc,
});

const lambdaDispatchStack = new LambdaDispatchStack(app, 'lambda-dispatch', {
  env: devEnv,
  stackName: 'lambda-dispatch-app-stack',
  vpc: vpcStack.vpc,
  loadBalancerArn: cdk.Fn.importValue('lambda-dispatch-ecs-LoadBalancerArn'),
  httpsListenerArn: cdk.Fn.importValue('lambda-dispatch-ecs-HttpsListenerArn'),
  loadBalancerSecurityGroupId: cdk.Fn.importValue(
    'lambda-dispatch-ecs-LoadBalancerSecurityGroupId',
  ),
  loadBalancerDnsName: cdk.Fn.importValue('lambda-dispatch-ecs-ALBDnsName'),
  loadBalancerHostedZoneId: cdk.Fn.importValue('lambda-dispatch-ecs-ALBCanonicalHostedZoneId'),
  ecsClusterArn: cdk.Fn.importValue('lambda-dispatch-ecs-ClusterArn'),
  ecsClusterName: cdk.Fn.importValue('lambda-dispatch-ecs-ClusterName'),
});
cdk.Tags.of(lambdaDispatchStack).add('Name', 'lambda-dispatch');

const lambdaDispatchStackPr = new LambdaDispatchStack(app, 'lambda-dispatch-pr', {
  env: devEnv,
  stackName: `lambda-dispatch-app-stack-pr-${process.env.PR_NUMBER}`,
  vpc: vpcStack.vpc,
  loadBalancerArn: cdk.Fn.importValue('lambda-dispatch-ecs-LoadBalancerArn'),
  httpsListenerArn: cdk.Fn.importValue('lambda-dispatch-ecs-HttpsListenerArn'),
  loadBalancerSecurityGroupId: cdk.Fn.importValue(
    'lambda-dispatch-ecs-LoadBalancerSecurityGroupId',
  ),
  loadBalancerDnsName: cdk.Fn.importValue('lambda-dispatch-ecs-ALBDnsName'),
  loadBalancerHostedZoneId: cdk.Fn.importValue('lambda-dispatch-ecs-ALBCanonicalHostedZoneId'),
  ecsClusterArn: cdk.Fn.importValue('lambda-dispatch-ecs-ClusterArn'),
  ecsClusterName: cdk.Fn.importValue('lambda-dispatch-ecs-ClusterName'),
  removalPolicy: cdk.RemovalPolicy.DESTROY,
});
cdk.Tags.of(lambdaDispatchStackPr).add('Name', `lambda-dispatch-pr-${process.env.PR_NUMBER}`);

app.synth();
