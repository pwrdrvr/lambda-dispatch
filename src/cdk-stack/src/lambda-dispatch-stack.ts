import { LambdaDispatchFunction, LambdaDispatchECS } from '@pwrdrvr/lambda-dispatch-construct';
import * as cdk from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import * as ecs from 'aws-cdk-lib/aws-ecs';
import * as elbv2 from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as route53 from 'aws-cdk-lib/aws-route53';
import * as route53targets from 'aws-cdk-lib/aws-route53-targets';
import { Construct } from 'constructs';

export interface LambdaDispatchStackProps extends cdk.StackProps {
  /**
   * VPC where the ECS service will be deployed
   */
  readonly vpc: ec2.IVpc;

  /**
   * Application Load Balancer for the ECS service
   */
  readonly loadBalancerArn: string;

  /**
   * HTTPS listener for the Application Load Balancer
   */
  readonly httpsListenerArn: string;

  /**
   * Security group for the load balancer
   */
  readonly loadBalancerSecurityGroupId: string;

  /**
   * Hosted zone ID for ALB
   */
  readonly loadBalancerHostedZoneId: string;

  /**
   * DNS name for ALB
   */
  readonly loadBalancerDnsName: string;

  /**
   * ECS Cluster ARN
   */
  readonly ecsClusterArn: string;

  /**
   * ECS Cluster Name
   */
  readonly ecsClusterName: string;
}

export class LambdaDispatchStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props: LambdaDispatchStackProps) {
    super(scope, id, props);

    const { vpc } = props;

    const loadBalancer = elbv2.ApplicationLoadBalancer.fromApplicationLoadBalancerAttributes(
      this,
      'ImportedALB',
      {
        loadBalancerArn: props.loadBalancerArn,
        securityGroupId: props.loadBalancerSecurityGroupId,
        vpc,
        loadBalancerCanonicalHostedZoneId: props.loadBalancerHostedZoneId,
        loadBalancerDnsName: props.loadBalancerDnsName,
      },
    );

    const httpsListener = elbv2.ApplicationListener.fromApplicationListenerAttributes(
      this,
      'ImportedHttpsListener',
      {
        listenerArn: props.httpsListenerArn,
        securityGroup: ec2.SecurityGroup.fromSecurityGroupId(
          this,
          'ImportedHttpsListenerSG',
          props.loadBalancerSecurityGroupId,
        ),
        defaultPort: 443,
      },
    );

    // const cluster = ecs.Cluster.fromClusterArn(this, 'ImportedCluster', props.ecsClusterArn);
    const cluster = ecs.Cluster.fromClusterAttributes(this, 'ImportedCluster', {
      clusterArn: props.ecsClusterArn,
      vpc: props.vpc,
      clusterName: props.ecsClusterName,
    });

    // Create Lambda construct
    const lambdaConstruct = new LambdaDispatchFunction(this, 'LambdaConstruct', {
      vpc,
      architecture: lambda.Architecture.ARM_64,
      memorySize: 1769,
      dockerImage: lambda.DockerImageCode.fromEcr(
        ecr.Repository.fromRepositoryName(this, 'LambdaRepo', 'lambda-dispatch-demo-app'),
        {
          tagOrDigest: process.env.PR_NUMBER ? `pr-${process.env.PR_NUMBER}-arm64` : 'latest',
        },
      ),
    });

    // Create ECS construct
    const ecsConstruct = new LambdaDispatchECS(this, 'EcsConstruct', {
      vpc,
      lambdaFunction: lambdaConstruct.function,
      cluster,
      containerImage: process.env.PR_NUMBER
        ? ecs.ContainerImage.fromEcrRepository(
          ecr.Repository.fromRepositoryName(this, 'EcsRepo', 'lambda-dispatch-router'),
          `pr-${process.env.PR_NUMBER}`,
        )
        : undefined,
    });

    // Allow ECS tasks to invoke Lambda
    lambdaConstruct.function.grantInvoke(ecsConstruct.service.taskDefinition.taskRole);

    const hostname = `lambdadispatch${process.env.PR_NUMBER ? `-pr-${process.env.PR_NUMBER}` : ''}`;

    // Add the target group to the HTTPS listener
    httpsListener.addTargetGroups('EcsTargetGroup', {
      targetGroups: [ecsConstruct.targetGroup],
      conditions: [elbv2.ListenerCondition.hostHeaders([`${hostname}.ghpublic.pwrdrvr.com`])],
      // Set the priority to the PR number or 49999 if not a PR
      priority: process.env.PR_NUMBER ? parseInt(process.env.PR_NUMBER) : 49999,
    });

    // Create Route53 records
    const hostedZone = route53.HostedZone.fromHostedZoneAttributes(this, 'HostedZone', {
      hostedZoneId: 'Z005084420J9MD9JNBCUK',
      zoneName: 'ghpublic.pwrdrvr.com',
    });
    new route53.ARecord(this, 'LambdaDispatchRecord', {
      zone: hostedZone,
      recordName: hostname,
      target: route53.RecordTarget.fromAlias(new route53targets.LoadBalancerTarget(loadBalancer)),
    });

    // Output the VPC ID
    new cdk.CfnOutput(this, 'VpcId', {
      value: vpc.vpcId,
      description: 'VPC ID',
    });
  }
}
