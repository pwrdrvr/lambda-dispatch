import * as cdk from 'aws-cdk-lib';
import * as acm from 'aws-cdk-lib/aws-certificatemanager';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecs from 'aws-cdk-lib/aws-ecs';
import * as elbv2 from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import { Construct } from 'constructs';

export interface EcsClusterStackProps extends cdk.StackProps {
  /**
   * VPC where the ECS service will be deployed
   */
  readonly vpc: ec2.IVpc;
}

export class EcsClusterStack extends cdk.Stack {
  // Expose the ALB as a public property
  public readonly loadBalancer: elbv2.IApplicationLoadBalancer;

  // Expose the HTTPS listener as a public property
  public readonly httpsListener: elbv2.IApplicationListener;

  // Expose the NLB
  public readonly networkLoadBalancer: elbv2.INetworkLoadBalancer;

  constructor(scope: Construct, id: string, props: EcsClusterStackProps) {
    super(scope, id, props);

    // Create Application Load Balancer
    const securityGroup = new ec2.SecurityGroup(this, 'LoadBalancerSG', {
      vpc: props.vpc,
      allowAllOutbound: true,
      description: 'Security Group for the Application Load Balancer',
    });
    this.loadBalancer = new elbv2.ApplicationLoadBalancer(this, 'LoadBalancer', {
      vpc: props.vpc,
      internetFacing: true,
      vpcSubnets: { subnetType: ec2.SubnetType.PUBLIC },
      securityGroup,
    });

    // Import certificate
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
      vpc: props.vpc,
      enableFargateCapacityProviders: true,
    });

    //
    // Create Network Load Balancer
    //
    this.networkLoadBalancer = new elbv2.NetworkLoadBalancer(this, 'NetworkLoadBalancer', {
      vpc: props.vpc,
      internetFacing: true,
      crossZoneEnabled: true,
      vpcSubnets: { subnetType: ec2.SubnetType.PUBLIC },
      securityGroups: [securityGroup],
    });

    // Output the VPC ID
    new cdk.CfnOutput(this, 'VpcId', {
      value: props.vpc.vpcId,
      description: 'VPC ID',
      exportName: `${this.stackName}-VpcId`,
    });

    // Output the Load Balancer ARN
    new cdk.CfnOutput(this, 'LoadBalancerArn', {
      value: this.loadBalancer.loadBalancerArn,
      exportName: `${this.stackName}-LoadBalancerArn`,
    });

    // Output the Network Load Balancer ARN
    new cdk.CfnOutput(this, 'NetworkLoadBalancerArn', {
      value: this.networkLoadBalancer.loadBalancerArn,
      exportName: `${this.stackName}-NetworkLoadBalancerArn`,
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
    new cdk.CfnOutput(this, 'NetworkCanonicalHostedZoneId', {
      value: this.networkLoadBalancer.loadBalancerCanonicalHostedZoneId,
      exportName: `${this.stackName}-NetworkALBCanonicalHostedZoneId`,
    });

    new cdk.CfnOutput(this, 'LoadBalancerDnsName', {
      value: this.loadBalancer.loadBalancerDnsName,
      exportName: `${this.stackName}-ALBDnsName`,
    });
    new cdk.CfnOutput(this, 'NetworkALBDnsName', {
      value: this.networkLoadBalancer.loadBalancerDnsName,
      exportName: `${this.stackName}-NetworkALBDnsName`,
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
