import * as cdk from "aws-cdk-lib";
import * as ec2 from "aws-cdk-lib/aws-ec2";
import { FckNatInstanceProvider } from "cdk-fck-nat";
import { Construct } from "constructs";

export class VpcStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    // Create VPC with custom NAT Gateways
    // ~$3.24/mo + ~$3.60/mo for IPv4 addr instead of $32.40/mo
    const vpc = new ec2.Vpc(this, "vpc", {
      maxAzs: 1,
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
      natGatewayProvider: new FckNatInstanceProvider({
        instanceType: ec2.InstanceType.of(
          ec2.InstanceClass.T4G,
          ec2.InstanceSize.NANO
        ),
        keyPair: ec2.KeyPair.fromKeyPairName(
          this,
          "hhuntaro-2024",
          "huntharo-2024"
        ),
      }),
    });

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

    // Output the VPC ID
    new cdk.CfnOutput(this, "VpcId", {
      value: vpc.vpcId,
      description: "VPC ID",
    });
  }
}
