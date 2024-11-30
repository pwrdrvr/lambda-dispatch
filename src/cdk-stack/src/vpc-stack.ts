import * as cdk from 'aws-cdk-lib';
import * as acm from 'aws-cdk-lib/aws-certificatemanager';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecs from 'aws-cdk-lib/aws-ecs';
import * as elbv2 from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import { FckNatInstanceProvider } from 'cdk-fck-nat';
import { Construct } from 'constructs';

export class VpcStack extends cdk.Stack {
  // Expose the VPC as a public property
  public readonly vpc: ec2.Vpc;

  // Explose the ALB as a public property
  public readonly loadBalancer: elbv2.IApplicationLoadBalancer;

  // Expose the HTTPS listener as a public property
  public readonly httpsListener: elbv2.IApplicationListener;

  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    const natGatewayProvider = new FckNatInstanceProvider({
      instanceType: ec2.InstanceType.of(ec2.InstanceClass.T4G, ec2.InstanceSize.NANO),
      keyPair: new ec2.KeyPair(this, 'key-pair'),
    });

    // Create VPC with custom NAT Gateways
    // ~$3.24/mo + ~$3.60/mo for IPv4 addr instead of $32.40/mo
    this.vpc = new ec2.Vpc(this, 'vpc', {
      maxAzs: 2,
      ipAddresses: ec2.IpAddresses.cidr('10.0.0.0/16'),
      enableDnsHostnames: true,
      enableDnsSupport: true,
      subnetConfiguration: [
        {
          cidrMask: 24,
          name: 'Public',
          subnetType: ec2.SubnetType.PUBLIC,
        },
        {
          cidrMask: 24,
          name: 'Private',
          subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS,
        },
      ],
      natGatewayProvider,
    });

    natGatewayProvider.connections.allowFrom(
      ec2.Peer.ipv4(this.vpc.vpcCidrBlock),
      ec2.Port.allTraffic(),
    );

    // Add S3 Gateway Endpoint
    new ec2.GatewayVpcEndpoint(this, 'S3GatewayEndpoint', {
      vpc: this.vpc,
      service: ec2.GatewayVpcEndpointAwsService.S3,
    });

    // Add DynamoDB Gateway Endpoint
    new ec2.GatewayVpcEndpoint(this, 'DynamoDBGatewayEndpoint', {
      vpc: this.vpc,
      service: ec2.GatewayVpcEndpointAwsService.DYNAMODB,
    });

    // Create Application Load Balancer
    const securityGroup = new ec2.SecurityGroup(this, 'LoadBalancerSG', {
      vpc: this.vpc,
      allowAllOutbound: true,
      description: 'Security Group for the Application Load Balancer',
    });
    this.loadBalancer = new elbv2.ApplicationLoadBalancer(this, 'LoadBalancer', {
      vpc: this.vpc,
      internetFacing: true,
      vpcSubnets: { subnetType: ec2.SubnetType.PUBLIC },
      securityGroup,
    });

    // Create certificate
    // const certificate = new acm.Certificate(this, 'Certificate', {
    //   domainName: 'lambdadispatch.ghpublic.pwrdrvr.com',
    //   subjectAlternativeNames: [
    //     'directlambda.ghpublic.pwrdrvr.com',
    //     'directlambdaalias.ghpublic.pwrdrvr.com',
    //   ],
    //   validation: acm.CertificateValidation.fromDns(),
    // });
    const certificate = acm.Certificate.fromCertificateArn(
      this,
      'Certificate',
      'arn:aws:acm:us-east-2:220761759939:certificate/ba705c87-4af6-4005-9d6d-d51318fdcbeb',
    );

    // Add HTTP listener
    this.loadBalancer.addListener('HttpListener', {
      port: 80,
      open: true,
      defaultAction: elbv2.ListenerAction.redirect({
        protocol: 'HTTPS',
        port: '443',
      }),
    });

    // Add HTTPS listener
    this.httpsListener = this.loadBalancer.addListener('HttpsListener', {
      port: 443,
      certificates: [certificate],
      sslPolicy: elbv2.SslPolicy.TLS13_RES,
      open: true,
      defaultAction: elbv2.ListenerAction.fixedResponse(404, {
        contentType: 'text/plain',
        messageBody: 'Could not find the requested resource',
      }),
    });

    // Create cluster with appropriate capacity providers
    const cluster = new ecs.Cluster(this, 'EcsCluster', {
      vpc: this.vpc,
      enableFargateCapacityProviders: true,
    });

    // Output the VPC ID
    new cdk.CfnOutput(this, 'VpcId', {
      value: this.vpc.vpcId,
      description: 'VPC ID',
      exportName: `${this.stackName}-VpcId`,
    });

    // Output the Load Balancer ARN
    new cdk.CfnOutput(this, 'LoadBalancerArn', {
      value: this.loadBalancer.loadBalancerArn,
      exportName: `${this.stackName}-LoadBalancerArn`,
    });

    // Output the HTTPS Listener ARN
    new cdk.CfnOutput(this, 'HttpsListenerArn', {
      value: this.httpsListener.listenerArn,
      exportName: `${this.stackName}-HttpsListenerArn`,
    });

    // Output the security group ID
    new cdk.CfnOutput(this, 'SecurityGroupId', {
      value: securityGroup.securityGroupId,
      exportName: `${this.stackName}-LoadBalancerSecurityGroupId`,
    });

    // Output the canonical hosted zone ID
    new cdk.CfnOutput(this, 'CanonicalHostedZoneId', {
      value: this.loadBalancer.loadBalancerCanonicalHostedZoneId,
      exportName: `${this.stackName}-ALBCanonicalHostedZoneId`,
    });

    new cdk.CfnOutput(this, 'LoadBalancerDnsName', {
      value: this.loadBalancer.loadBalancerDnsName,
      exportName: `${this.stackName}-ALBDnsName`,
    });

    // Output the cluster ARN
    new cdk.CfnOutput(this, 'ClusterArn', {
      value: cluster.clusterArn,
      exportName: `${this.stackName}-ClusterArn`,
    });
    new cdk.CfnOutput(this, 'ClusterName', {
      value: cluster.clusterArn,
      exportName: `${this.stackName}-ClusterName`,
    });
  }
}
