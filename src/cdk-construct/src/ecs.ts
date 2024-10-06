import * as cdk from "aws-cdk-lib";
import * as ec2 from "aws-cdk-lib/aws-ec2";
import * as ecr from "aws-cdk-lib/aws-ecr";
import * as ecs from "aws-cdk-lib/aws-ecs";
import * as iam from "aws-cdk-lib/aws-iam";
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as logs from "aws-cdk-lib/aws-logs";
import * as elbv2 from "aws-cdk-lib/aws-elasticloadbalancingv2";
import { Construct } from "constructs";

export interface EcsConstructProps {
  readonly vpc: ec2.IVpc;
  readonly lambdaFunction: lambda.IFunction;
  readonly loadBalancer: elbv2.IApplicationLoadBalancer;
  readonly httpsListener: elbv2.IApplicationListener;
}

export class EcsConstruct extends Construct {
  public readonly service: ecs.FargateService;
  public readonly securityGroup: ec2.SecurityGroup;
  public readonly targetGroup: elbv2.ApplicationTargetGroup;

  constructor(scope: Construct, id: string, props: EcsConstructProps) {
    super(scope, id);

    const cluster = new ecs.Cluster(this, "EcsCluster", {
      vpc: props.vpc,
    });

    const taskRole = new iam.Role(this, "EcsTaskRole", {
      assumedBy: new iam.ServicePrincipal("ecs-tasks.amazonaws.com"),
    });

    taskRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ["lambda:InvokeFunction"],
        resources: [props.lambdaFunction.functionArn],
      })
    );

    const taskDefinition = new ecs.FargateTaskDefinition(
      this,
      "TaskDefinition",
      {
        memoryLimitMiB: 2048,
        cpu: 1024,
        taskRole: taskRole,
        runtimePlatform: {
          cpuArchitecture: ecs.CpuArchitecture.ARM64,
          operatingSystemFamily: ecs.OperatingSystemFamily.LINUX,
        },
      }
    );

    const logGroup = new logs.LogGroup(this, "ServiceLogGroup", {
      retention: logs.RetentionDays.ONE_WEEK,
    });

    const container = taskDefinition.addContainer("LambdaDispatchRouter", {
      image: ecs.ContainerImage.fromEcrRepository(
        ecr.Repository.fromRepositoryName(
          this,
          "EcsRepo",
          "lambda-dispatch-router"
        ),
        "latest"
      ),
      logging: ecs.LogDriver.awsLogs({
        logGroup: logGroup,
        streamPrefix: "ecs",
      }),
      environment: {
        DOTNET_ThreadPool_UnfairSemaphoreSpinLimit: "0",
        LAMBDA_DISPATCH_MaxWorkerThreads: "2",
        LAMBDA_DISPATCH_FunctionName: props.lambdaFunction.functionArn,
        LAMBDA_DISPATCH_MaxConcurrentCount: "10",
        LAMBDA_DISPATCH_AllowInsecureControlChannel: "true",
        LAMBDA_DISPATCH_PreferredControlChannelScheme: "http",
      },
    });

    container.addPortMappings(
      { containerPort: 5001, protocol: ecs.Protocol.TCP },
      { containerPort: 5003, protocol: ecs.Protocol.TCP },
      { containerPort: 5004, protocol: ecs.Protocol.TCP }
    );

    this.securityGroup = new ec2.SecurityGroup(this, "EcsSecurityGroup", {
      vpc: props.vpc,
      allowAllOutbound: true,
      description: "Security Group for ECS Fargate tasks",
    });

    // Add inbound rules for ports 5003 and 5004
    this.securityGroup.addIngressRule(
      ec2.Peer.ipv4(props.vpc.vpcCidrBlock),
      ec2.Port.tcp(5003),
      "Allow inbound traffic on port 5003 from within the VPC (HTTP from Lambda)"
    );
    this.securityGroup.addIngressRule(
      ec2.Peer.ipv4(props.vpc.vpcCidrBlock),
      ec2.Port.tcp(5004),
      "Allow inbound traffic on port 5004 from within the VPC (HTTPS from Lambda)"
    );

    this.targetGroup = new elbv2.ApplicationTargetGroup(
      this,
      "FargateTargetGroup",
      {
        vpc: props.vpc,
        port: 5001,
        protocol: elbv2.ApplicationProtocol.HTTP,
        targetType: elbv2.TargetType.IP,
        healthCheck: {
          path: "/health",
          interval: cdk.Duration.seconds(5),
          timeout: cdk.Duration.seconds(2),
          healthyThresholdCount: 2,
        },
      }
    );

    this.service = new ecs.FargateService(this, "EcsService", {
      cluster: cluster,
      taskDefinition: taskDefinition,
      desiredCount: 1,
      assignPublicIp: false,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [this.securityGroup],
    });

    // Add auto-scaling
    const scaling = this.service.autoScaleTaskCount({
      maxCapacity: 10,
      minCapacity: 1,
    });
    scaling.scaleOnCpuUtilization("CpuScaling", {
      targetUtilizationPercent: 50,
      scaleInCooldown: cdk.Duration.seconds(60),
      scaleOutCooldown: cdk.Duration.seconds(60),
    });

    // Attach the service to the target group
    this.service.attachToApplicationTargetGroup(this.targetGroup);

    // Add the target group to the HTTPS listener
    props.httpsListener.addTargetGroups("EcsTargetGroup", {
      targetGroups: [this.targetGroup],
      priority: 10,
      conditions: [
        elbv2.ListenerCondition.hostHeaders([
          "lambdadispatch.ghpublic.pwrdrvr.com",
        ]),
      ],
    });
  }
}
