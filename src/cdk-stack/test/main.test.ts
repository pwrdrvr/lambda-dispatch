import { App } from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';
import { LambdaDispatchStack } from '../src/lambda-dispatch-stack';
import { VpcStack } from '../src/vpc-stack';

test('Snapshot', () => {
  const app = new App();

  // Create a simple VPC for testing
  const vpcStack = new VpcStack(app, 'vpc-stack', {
    env: {
      region: 'us-east-1',
      account: '123456789012',
    },
  });

  const stack = new LambdaDispatchStack(app, 'test', {
    env: {
      region: 'us-east-1',
      account: '123456789012',
    },
    vpc: vpcStack.vpc,
    loadBalancerArn:
      'arn:aws:elasticloadbalancing:us-east-1:123456789012:loadbalancer/app/my-load-balancer/50dc6c495c0c9188',
    httpsListenerArn:
      'arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/my-load-balancer/50dc6c495c0c9188/0467ef3c5c1e9977',
    loadBalancerSecurityGroupId: 'sg-0123456789abcdef0',
    loadBalancerDnsName: 'my-load-balancer-1234567890.us-east-1.elb.amazonaws.com',
    loadBalancerHostedZoneId: 'Z3DZXE0Q79N41H',
    ecsClusterArn: 'arn:aws:ecs:us-east-1:123456789012:cluster/my-cluster',
    ecsClusterName: 'my-cluster',
  });

  const template = Template.fromStack(stack);
  const templateJson = template.toJSON();

  // Remove the ImageURI so tests will pass on CI
  if (templateJson.Resources.LambdaConstructLambdaFunction8A3CAE86.Properties.Code) {
    delete templateJson.Resources.LambdaConstructLambdaFunction8A3CAE86.Properties.Code.ImageUri;
  }

  expect(templateJson).toMatchSnapshot();
});
