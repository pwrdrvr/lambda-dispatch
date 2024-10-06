import * as cdk from "aws-cdk-lib";
import * as ec2 from "aws-cdk-lib/aws-ec2";
import * as ecr from "aws-cdk-lib/aws-ecr";
import * as iam from "aws-cdk-lib/aws-iam";
import * as lambda from "aws-cdk-lib/aws-lambda";
import { Construct } from "constructs";

export interface LambdaConstructProps {
  readonly vpc: ec2.IVpc;
  readonly ecsSecurityGroup?: ec2.ISecurityGroup;
}

export class LambdaConstruct extends Construct {
  public readonly function: lambda.Function;

  constructor(scope: Construct, id: string, props: LambdaConstructProps) {
    super(scope, id);

    const lambdaRole = new iam.Role(this, "LambdaExecutionRole", {
      assumedBy: new iam.ServicePrincipal("lambda.amazonaws.com"),
      managedPolicies: [
        iam.ManagedPolicy.fromAwsManagedPolicyName(
          "service-role/AWSLambdaVPCAccessExecutionRole"
        ),
      ],
    });

    const lambdaSG = new ec2.SecurityGroup(this, "LambdaSG", {
      vpc: props.vpc,
      allowAllOutbound: true,
      description: "Security Group for Lambda function",
    });

    if (props.ecsSecurityGroup) {
      lambdaSG.addIngressRule(
        props.ecsSecurityGroup,
        ec2.Port.allTcp(),
        "Allow inbound from ECS tasks"
      );
    }

    this.function = new lambda.DockerImageFunction(this, "LambdaFunction", {
      code: lambda.DockerImageCode.fromEcr(
        ecr.Repository.fromRepositoryName(
          this,
          "LambdaRepo",
          "lambda-dispatch-demo-app"
        ),
        {
          tagOrDigest: "latest",
        }
      ),
      vpc: props.vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [lambdaSG],
      memorySize: 192,
      timeout: cdk.Duration.seconds(60),
      architecture: lambda.Architecture.ARM_64,
      role: lambdaRole,
      environment: {
        LAMBDA_DISPATCH_RUNTIME: "current_thread",
      },
    });
  }
}
