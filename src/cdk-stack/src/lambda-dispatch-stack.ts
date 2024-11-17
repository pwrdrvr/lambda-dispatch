import { LambdaConstruct, EcsConstruct } from '@pwrdrvr/lambda-dispatch-construct';
import * as cdk from 'aws-cdk-lib';
import * as acm from 'aws-cdk-lib/aws-certificatemanager';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import * as elbv2 from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import * as route53 from 'aws-cdk-lib/aws-route53';
import * as route53targets from 'aws-cdk-lib/aws-route53-targets';
import { Construct } from 'constructs';

export interface LambdaDispatchStackProps extends cdk.StackProps {
  readonly vpc: ec2.IVpc;
}

export class LambdaDispatchStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props: LambdaDispatchStackProps) {
    super(scope, id, props);

    const { vpc } = props;

    // Reference ECR repositories from the other stack
    ecr.Repository.fromRepositoryName(
      this,
      'ImportedLambdaRepo',
      cdk.Fn.importValue('LambdaRepoName'),
    );

    ecr.Repository.fromRepositoryName(this, 'ImportedEcsRepo', cdk.Fn.importValue('EcsRepoName'));

    // Create Application Load Balancer
    const alb = new elbv2.ApplicationLoadBalancer(this, 'ECSFargateLoadBalancer', {
      vpc: vpc,
      internetFacing: true,
      vpcSubnets: { subnetType: ec2.SubnetType.PUBLIC },
    });

    // Create certificate
    const certificate = new acm.Certificate(this, 'Certificate', {
      domainName: 'lambdadispatch.ghpublic.pwrdrvr.com',
      subjectAlternativeNames: [
        'directlambda.ghpublic.pwrdrvr.com',
        'directlambdaalias.ghpublic.pwrdrvr.com',
      ],
      validation: acm.CertificateValidation.fromDns(),
    });

    // Add HTTP listener
    alb.addListener('HttpListener', {
      port: 80,
      open: true,
      defaultAction: elbv2.ListenerAction.redirect({
        protocol: 'HTTPS',
        port: '443',
      }),
    });

    // Add HTTPS listener
    const httpsListener = alb.addListener('HttpsListener', {
      port: 443,
      certificates: [certificate],
      sslPolicy: elbv2.SslPolicy.TLS13_RES,
      open: true,
      defaultAction: elbv2.ListenerAction.fixedResponse(404, {
        contentType: 'text/plain',
        messageBody: 'Could not find the requested resource',
      }),
    });

    // Create Lambda construct
    const lambdaConstruct = new LambdaConstruct(this, 'LambdaConstruct', {
      vpc,
    });

    // Create ECS construct
    const ecsConstruct = new EcsConstruct(this, 'EcsConstruct', {
      vpc: vpc,
      lambdaFunction: lambdaConstruct.function,
      loadBalancer: alb,
      httpsListener,
    });

    // Allow ECS tasks to invoke Lambda
    lambdaConstruct.function.grantInvoke(ecsConstruct.service.taskDefinition.taskRole);

    // Create Route53 records
    const hostedZone = route53.HostedZone.fromHostedZoneAttributes(this, 'HostedZone', {
      hostedZoneId: 'Z005084420J9MD9JNBCUK',
      zoneName: 'ghpublic.pwrdrvr.com',
    });

    new route53.ARecord(this, 'LambdaDispatchRecord', {
      zone: hostedZone,
      recordName: 'lambdadispatch',
      target: route53.RecordTarget.fromAlias(new route53targets.LoadBalancerTarget(alb)),
    });
    // Output the VPC ID
    new cdk.CfnOutput(this, 'VpcId', {
      value: vpc.vpcId,
      description: 'VPC ID',
    });
  }
}
