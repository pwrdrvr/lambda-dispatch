import * as cdk from "aws-cdk-lib";
import * as ec2 from "aws-cdk-lib/aws-ec2";
import * as ecr from "aws-cdk-lib/aws-ecr";
import * as elbv2 from "aws-cdk-lib/aws-elasticloadbalancingv2";
import * as route53 from "aws-cdk-lib/aws-route53";
import * as route53targets from "aws-cdk-lib/aws-route53-targets";
import * as acm from "aws-cdk-lib/aws-certificatemanager";
import { FckNatInstanceProvider } from "cdk-fck-nat";
import { Construct } from "constructs";
import {
  LambdaConstruct,
  EcsConstruct,
} from "@pwrdrvr/lambda-dispatch-construct";

export class LambdaDispatchStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    const natGatewayProvider = new FckNatInstanceProvider({
      instanceType: ec2.InstanceType.of(
        ec2.InstanceClass.T4G,
        ec2.InstanceSize.NANO
      ),
      keyPair: ec2.KeyPair.fromKeyPairName(
        this,
        "hhuntaro-2024",
        "huntharo-2024"
      ),
    });

    // Create VPC with custom NAT Gateways
    // ~$3.24/mo + ~$3.60/mo for IPv4 addr instead of $32.40/mo
    const vpc = new ec2.Vpc(this, "vpc", {
      maxAzs: 2,
      ipAddresses: ec2.IpAddresses.cidr("10.0.0.0/16"),
      enableDnsHostnames: true,
      enableDnsSupport: true,
      subnetConfiguration: [
        {
          cidrMask: 24,
          name: "Public",
          subnetType: ec2.SubnetType.PUBLIC,
        },
        {
          cidrMask: 24,
          name: "Private",
          subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS,
        },
      ],
      natGatewayProvider,
    });

    natGatewayProvider.connections.allowFrom(
      ec2.Peer.ipv4(vpc.vpcCidrBlock),
      ec2.Port.allTraffic()
    );

    // Add S3 Gateway Endpoint
    new ec2.GatewayVpcEndpoint(this, "S3GatewayEndpoint", {
      vpc,
      service: ec2.GatewayVpcEndpointAwsService.S3,
    });

    // Add DynamoDB Gateway Endpoint
    new ec2.GatewayVpcEndpoint(this, "DynamoDBGatewayEndpoint", {
      vpc,
      service: ec2.GatewayVpcEndpointAwsService.DYNAMODB,
    });

    // Reference ECR repositories from the other stack
    ecr.Repository.fromRepositoryName(
      this,
      "ImportedLambdaRepo",
      cdk.Fn.importValue("LambdaRepoName")
    );

    ecr.Repository.fromRepositoryName(
      this,
      "ImportedEcsRepo",
      cdk.Fn.importValue("EcsRepoName")
    );

    // Create Application Load Balancer
    const alb = new elbv2.ApplicationLoadBalancer(
      this,
      "ECSFargateLoadBalancer",
      {
        vpc: vpc,
        internetFacing: true,
        vpcSubnets: { subnetType: ec2.SubnetType.PUBLIC },
      }
    );

    // Create certificate
    const certificate = new acm.Certificate(this, "Certificate", {
      domainName: "lambdadispatch.ghpublic.pwrdrvr.com",
      subjectAlternativeNames: [
        "directlambda.ghpublic.pwrdrvr.com",
        "directlambdaalias.ghpublic.pwrdrvr.com",
      ],
      validation: acm.CertificateValidation.fromDns(),
    });

    // Add HTTP listener
    alb.addListener("HttpListener", {
      port: 80,
      open: true,
      defaultAction: elbv2.ListenerAction.redirect({
        protocol: "HTTPS",
        port: "443",
      }),
    });

    // Add HTTPS listener
    const httpsListener = alb.addListener("HttpsListener", {
      port: 443,
      certificates: [certificate],
      sslPolicy: elbv2.SslPolicy.TLS13_RES,
      open: true,
      defaultAction: elbv2.ListenerAction.fixedResponse(404, {
        contentType: "text/plain",
        messageBody: "Could not find the requested resource",
      }),
    });

    // Create Lambda construct
    const lambdaConstruct = new LambdaConstruct(this, "LambdaConstruct", {
      vpc,
    });

    // Create ECS construct
    const ecsConstruct = new EcsConstruct(this, "EcsConstruct", {
      vpc: vpc,
      lambdaFunction: lambdaConstruct.function,
      loadBalancer: alb,
      httpsListener,
    });

    // Allow ECS tasks to invoke Lambda
    lambdaConstruct.function.grantInvoke(
      ecsConstruct.service.taskDefinition.taskRole
    );

    // Create Route53 records
    const hostedZone = route53.HostedZone.fromHostedZoneAttributes(
      this,
      "HostedZone",
      {
        hostedZoneId: "Z005084420J9MD9JNBCUK",
        zoneName: "ghpublic.pwrdrvr.com",
      }
    );

    new route53.ARecord(this, "LambdaDispatchRecord", {
      zone: hostedZone,
      recordName: "lambdadispatch",
      target: route53.RecordTarget.fromAlias(
        new route53targets.LoadBalancerTarget(alb)
      ),
    });
    // Output the VPC ID
    new cdk.CfnOutput(this, "VpcId", {
      value: vpc.vpcId,
      description: "VPC ID",
    });
  }
}
