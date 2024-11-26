import * as cdk from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import * as ecs from 'aws-cdk-lib/aws-ecs';
import * as elbv2 from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import { Construct } from 'constructs';

/**
 * Properties for the ECS construct
 */
export interface EcsConstructProps {
  /**
   * VPC where the ECS service will be deployed
   */
  readonly vpc: ec2.IVpc;
  /**
   * Lambda function that will be invoked by the ECS service
   */
  readonly lambdaFunction: lambda.IFunction;
  /**
   * Application Load Balancer for the ECS service
   */
  readonly loadBalancer: elbv2.IApplicationLoadBalancer;
  /**
   * HTTPS listener for the Application Load Balancer
   */
  readonly httpsListener: elbv2.IApplicationListener;
  /**
   * Memory limit for the ECS task in MiB
   * @default 2048
   */
  readonly memoryLimitMiB?: number;
  /**
   * CPU units for the ECS task
   * @default 1024
   */
  readonly cpu?: number;
  /**
   * Maximum number of ECS tasks
   * @default 10
   */
  readonly maxCapacity?: number;
  /**
   * Minimum number of ECS tasks
   * @default 1
   */
  readonly minCapacity?: number;
  /**
   * Whether to use Fargate Spot capacity provider
   * Note: Fargate Spot only supports AMD64 architecture
   * @default false
   */
  readonly useFargateSpot?: boolean;
  /**
   * CPU architecture to use for the ECS tasks
   * Note: Fargate Spot only supports AMD64 architecture
   * @default ARM64
   */
  readonly cpuArchitecture?: ecs.CpuArchitecture;
}

/**
 * Creates an ECS service with the necessary configuration for Lambda Dispatch
 */
export class EcsConstruct extends Construct {
  /**
   * The ECS Fargate service
   */
  public readonly service: ecs.FargateService;
  /**
   * Security group for the ECS tasks
   */
  public readonly securityGroup: ec2.SecurityGroup;
  /**
   * Target group for the ECS service
   */
  public readonly targetGroup: elbv2.ApplicationTargetGroup;

  constructor(scope: Construct, id: string, props: EcsConstructProps) {
    super(scope, id);

    // Validate required props
    if (!props.vpc) {
      throw new Error('vpc is required in EcsConstructProps');
    }
    if (!props.lambdaFunction) {
      throw new Error('lambdaFunction is required in EcsConstructProps');
    }
    if (!props.loadBalancer) {
      throw new Error('loadBalancer is required in EcsConstructProps');
    }
    if (!props.httpsListener) {
      throw new Error('httpsListener is required in EcsConstructProps');
    }

    // Validate Fargate Spot and CPU architecture combination
    if (props.useFargateSpot && props.cpuArchitecture === ecs.CpuArchitecture.ARM64) {
      throw new Error('Fargate Spot only supports AMD64 architecture');
    }

    // Create cluster with appropriate capacity providers
    const cluster = new ecs.Cluster(this, 'EcsCluster', {
      vpc: props.vpc,
      enableFargateCapacityProviders: true,
    });

    const taskRole = new iam.Role(this, 'EcsTaskRole', {
      assumedBy: new iam.ServicePrincipal('ecs-tasks.amazonaws.com'),
    });

    taskRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ['lambda:InvokeFunction'],
        resources: [props.lambdaFunction.functionArn],
      }),
    );

    const taskDefinition = new ecs.FargateTaskDefinition(this, 'TaskDefinition', {
      memoryLimitMiB: props.memoryLimitMiB ?? 2048,
      cpu: props.cpu ?? 1024,
      taskRole: taskRole,
      runtimePlatform: {
        cpuArchitecture: props.cpuArchitecture ?? ecs.CpuArchitecture.ARM64,
        operatingSystemFamily: ecs.OperatingSystemFamily.LINUX,
      },
    });

    const logGroup = new logs.LogGroup(this, 'ServiceLogGroup', {
      retention: logs.RetentionDays.ONE_WEEK,
    });

    const container = taskDefinition.addContainer('LambdaDispatchRouter', {
      image: ecs.ContainerImage.fromEcrRepository(
        ecr.Repository.fromRepositoryName(this, 'EcsRepo', 'lambda-dispatch-router'),
        'latest',
      ),
      logging: ecs.LogDriver.awsLogs({
        logGroup: logGroup,
        streamPrefix: 'ecs',
      }),
      environment: {
        DOTNET_ThreadPool_UnfairSemaphoreSpinLimit: '0',
        LAMBDA_DISPATCH_MaxWorkerThreads: '2',
        LAMBDA_DISPATCH_FunctionName: props.lambdaFunction.functionArn,
        LAMBDA_DISPATCH_MaxConcurrentCount: '10',
        LAMBDA_DISPATCH_AllowInsecureControlChannel: 'true',
        LAMBDA_DISPATCH_PreferredControlChannelScheme: 'http',
      },
    });

    container.addPortMappings(
      { containerPort: 5001, protocol: ecs.Protocol.TCP },
      { containerPort: 5003, protocol: ecs.Protocol.TCP },
      { containerPort: 5004, protocol: ecs.Protocol.TCP },
    );

    this.securityGroup = new ec2.SecurityGroup(this, 'EcsSecurityGroup', {
      vpc: props.vpc,
      allowAllOutbound: true,
      description: 'Security Group for ECS Fargate tasks',
    });

    // Add inbound rules for ports 5003 and 5004
    this.securityGroup.addIngressRule(
      ec2.Peer.ipv4(props.vpc.vpcCidrBlock),
      ec2.Port.tcp(5003),
      'Allow inbound traffic on port 5003 from within the VPC (HTTP from Lambda)',
    );
    this.securityGroup.addIngressRule(
      ec2.Peer.ipv4(props.vpc.vpcCidrBlock),
      ec2.Port.tcp(5004),
      'Allow inbound traffic on port 5004 from within the VPC (HTTPS from Lambda)',
    );

    this.targetGroup = new elbv2.ApplicationTargetGroup(this, 'FargateTargetGroup', {
      vpc: props.vpc,
      port: 5001,
      protocol: elbv2.ApplicationProtocol.HTTP,
      targetType: elbv2.TargetType.IP,
      healthCheck: {
        path: '/health',
        interval: cdk.Duration.seconds(5),
        timeout: cdk.Duration.seconds(2),
        healthyThresholdCount: 2,
      },
    });

    // Configure capacity provider strategy based on props
    const capacityProviderStrategies = props.useFargateSpot
      ? [{ capacityProvider: 'FARGATE_SPOT', weight: 1 }]
      : [{ capacityProvider: 'FARGATE', weight: 1 }];

    this.service = new ecs.FargateService(this, 'EcsService', {
      cluster: cluster,
      taskDefinition: taskDefinition,
      desiredCount: props.minCapacity ?? 1,
      assignPublicIp: false,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [this.securityGroup],
      capacityProviderStrategies,
    });

    // Add auto-scaling
    const scaling = this.service.autoScaleTaskCount({
      maxCapacity: props.maxCapacity ?? 10,
      minCapacity: props.minCapacity ?? 1,
    });
    scaling.scaleOnCpuUtilization('CpuScaling', {
      targetUtilizationPercent: 50,
      scaleInCooldown: cdk.Duration.seconds(60),
      scaleOutCooldown: cdk.Duration.seconds(60),
    });

    // Attach the service to the target group
    this.service.attachToApplicationTargetGroup(this.targetGroup);

    // Add the target group to the HTTPS listener
    props.httpsListener.addTargetGroups('EcsTargetGroup', {
      targetGroups: [this.targetGroup],
      priority: 10,
      conditions: [elbv2.ListenerCondition.hostHeaders(['lambdadispatch.ghpublic.pwrdrvr.com'])],
    });
  }
}
