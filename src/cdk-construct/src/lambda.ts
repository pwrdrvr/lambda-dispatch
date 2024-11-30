import * as cdk from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import { Construct } from 'constructs';

/**
 * Properties for the Lambda construct
 */
export interface LambdaDispatchFunctionProps {
  /**
   * VPC where the Lambda function will be deployed
   */
  readonly vpc: ec2.IVpc;
  /**
   * Optional security group for ECS tasks that will invoke this Lambda
   */
  readonly ecsSecurityGroup?: ec2.ISecurityGroup;
  /**
   * Memory size for the Lambda function in MB
   * @default 192
   */
  readonly memorySize?: number;
  /**
   * Timeout for the Lambda function
   * @default 60 seconds
   */
  readonly timeout?: cdk.Duration;

  /**
   * CPU architecture for the Lambda function
   * @default ARM_64
   */
  readonly architecture?: lambda.Architecture;

  /**
   * Docker image for the Lambda function
   * @default - latest image from public ECR repository
   */
  readonly dockerImage?: lambda.DockerImageCode;
}

/**
 * Creates a Lambda function with the necessary configuration for Lambda Dispatch
 */
export class LambdaDispatchFunction extends Construct {
  /**
   * The Lambda function instance
   */
  public readonly function: lambda.Function;

  constructor(scope: Construct, id: string, props: LambdaDispatchFunctionProps) {
    super(scope, id);

    // Validate required props
    if (!props.vpc) {
      throw new Error('vpc is required in LambdaDispatchFunctionProps');
    }

    const lambdaRole = new iam.Role(this, 'LambdaExecutionRole', {
      assumedBy: new iam.ServicePrincipal('lambda.amazonaws.com'),
      managedPolicies: [
        iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AWSLambdaVPCAccessExecutionRole'),
      ],
    });

    const lambdaSG = new ec2.SecurityGroup(this, 'LambdaSG', {
      vpc: props.vpc,
      allowAllOutbound: true,
      description: 'Security Group for Lambda function',
    });

    if (props.ecsSecurityGroup) {
      lambdaSG.addIngressRule(
        props.ecsSecurityGroup,
        ec2.Port.allTcp(),
        'Allow inbound from ECS tasks',
      );
    }

    this.function = new lambda.DockerImageFunction(this, 'LambdaFunction', {
      code:
        props.dockerImage ??
        lambda.DockerImageCode.fromEcr(
          ecr.Repository.fromRepositoryName(this, 'LambdaRepo', 'lambda-dispatch-demo-app'),
          {
            tagOrDigest: 'latest',
          },
        ),
      vpc: props.vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [lambdaSG],
      memorySize: props.memorySize ?? 192,
      timeout: props.timeout ?? cdk.Duration.seconds(60),
      architecture: props.architecture ?? lambda.Architecture.ARM_64,
      role: lambdaRole,
      environment: {
        LAMBDA_DISPATCH_RUNTIME: 'current_thread',
      },
      logRetention: 7,
    });
  }
}
