import { App, Stack } from 'aws-cdk-lib';
import * as cdk from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import { LambdaDispatchFunction } from '../src/lambda';

test('LambdaDispatchFunction is created with correct properties', () => {
  const app = new App();
  const stack = new Stack(app, 'TestStack');
  const vpc = new ec2.Vpc(stack, 'TestVpc');
  const ecsSecurityGroup = new ec2.SecurityGroup(stack, 'EcsSecurityGroup', { vpc });

  new LambdaDispatchFunction(stack, 'TestLambdaDispatchFunction', {
    vpc,
    ecsSecurityGroup,
    memorySize: 256,
    timeout: cdk.Duration.seconds(120),
    architecture: lambda.Architecture.X86_64,
  });

  const template = Template.fromStack(stack);
  template.hasResourceProperties('AWS::Lambda::Function', {
    MemorySize: 256,
    Timeout: 120,
    Architectures: ['x86_64'],
  });
});

test('Security group is created and configured correctly', () => {
  const app = new App();
  const stack = new Stack(app, 'TestStack');
  const vpc = new ec2.Vpc(stack, 'TestVpc');
  const ecsSecurityGroup = new ec2.SecurityGroup(stack, 'EcsSecurityGroup', { vpc });

  new LambdaDispatchFunction(stack, 'TestLambdaDispatchFunction', {
    vpc,
    ecsSecurityGroup,
  });

  const template = Template.fromStack(stack);
  template.hasResourceProperties('AWS::EC2::SecurityGroup', {
    GroupDescription: 'Security Group for Lambda function',
  });
  template.hasResourceProperties('AWS::EC2::SecurityGroupIngress', {
    Description: 'Allow inbound from ECS tasks',
  });
});

test('Throws error when vpc is not provided', () => {
  const app = new App();
  const stack = new Stack(app, 'TestStack');

  expect(() => {
    new LambdaDispatchFunction(stack, 'TestLambdaDispatchFunction', {
      vpc: undefined as any,
    });
  }).toThrowError('vpc is required in LambdaDispatchFunctionProps');
});
