import * as cdk from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecs from 'aws-cdk-lib/aws-ecs';
import * as elbv2 from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import { Construct } from 'constructs';

/**
 * Port configuration for the Network Load Balancer
 */
export interface LambdaDispatchECSNlbPortConfiguration {
  /**
   * Port that the NLB will listen on for the Router container
   * @default 443
   */
  readonly routerPort: number;

  /**
   * Port that the NLB will listen on for the Demo App container
   * @default undefined
   */
  readonly demoAppPort: number;
}

/**
 * Properties for the ECS construct
 */
export interface LambdaDispatchECSProps {
  /**
   * VPC where the ECS service will be deployed
   */
  readonly vpc: ec2.IVpc;
  /**
   * Lambda function that will be invoked by the ECS service
   */
  readonly lambdaFunction: lambda.IFunction;
  /**
   * ECS Cluster
   */
  readonly cluster: ecs.ICluster;

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

  /**
   * Image for the lambda-dispatch Router
   * @default - latest image from public ECR repository
   */
  readonly routerImage?: ecs.ContainerImage;

  /**
   * Image for the demo app
   * This can be the same as the lambda image because it just won't start the lambda extension
   * @default - undefined
   */
  readonly demoAppImage?: ecs.ContainerImage;

  /**
   * The removal policy to apply to the log group
   *
   * @default - undefined
   */
  readonly removalPolicy?: cdk.RemovalPolicy;

  /**
   * Whether to create a Network Load Balancer in addition to the Application Load Balancer
   * @default false
   */
  readonly createNetworkLoadBalancer?: boolean;

  /**
   * Network Load Balancer
   */
  readonly networkLoadBalancer?: elbv2.INetworkLoadBalancer;

  /**
   * Certificate to use for HTTPS listeners on the Network Load Balancer
   * Required if createNetworkLoadBalancer is true
   */
  readonly nlbCertificate?: elbv2.IListenerCertificate;

  /**
   * Port configuration for the Network Load Balancer
   * These are the ports that the NLB will listen on and forward to the containers
   * @default - undefined
   */
  readonly nlbPorts?: LambdaDispatchECSNlbPortConfiguration;
}

/**
 * Creates an ECS service with the necessary configuration for Lambda Dispatch
 */
export class LambdaDispatchECS extends Construct {
  /**
   * The ECS Fargate service
   */
  public readonly service: ecs.FargateService;
  /**
   * Security group for the ECS tasks
   */
  public readonly securityGroup: ec2.SecurityGroup;
  /**
   * Application Load Balancer target group for the Router service
   */
  public readonly targetGroupRouter: elbv2.ApplicationTargetGroup;
  /**
   * Application Load Balancer target group for the Demo App service
   */
  public readonly targetGroupDemoApp: elbv2.ApplicationTargetGroup;

  /**
   * Network Load Balancer target group for the Router service
   * Only set if createNetworkLoadBalancer is true
   */
  public readonly nlbTargetGroupRouter?: elbv2.NetworkTargetGroup;
  /**
   * Network Load Balancer target group for the Demo App service
   * Only set if createNetworkLoadBalancer is true
   */
  public readonly nlbTargetGroupDemoApp?: elbv2.NetworkTargetGroup;

  /**
   * Network Load Balancer Router Listener
   * Only set if createNetworkLoadBalancer is true
   */
  public readonly nlbRouterListener?: elbv2.NetworkListener;

  /**
   * Network Load Balancer Demo App Listener
   * Only set if createNetworkLoadBalancer is true and demoAppPort is specified
   */
  public readonly nlbDemoAppListener?: elbv2.NetworkListener;

  constructor(scope: Construct, id: string, props: LambdaDispatchECSProps) {
    super(scope, id);

    // Validate required props
    if (!props.vpc) {
      throw new Error('vpc is required in LambdaDispatchECSProps');
    }
    if (!props.lambdaFunction) {
      throw new Error('lambdaFunction is required in LambdaDispatchECSProps');
    }

    // Validate Fargate Spot and CPU architecture combination
    if (props.useFargateSpot && props.cpuArchitecture === ecs.CpuArchitecture.ARM64) {
      throw new Error('Fargate Spot only supports AMD64 architecture');
    }

    // Validate NLB-specific props
    if (props.createNetworkLoadBalancer && !props.networkLoadBalancer) {
      throw new Error('networkLoadBalancer is required when createNetworkLoadBalancer is true');
    }
    if (props.createNetworkLoadBalancer && !props.nlbCertificate) {
      throw new Error('nlbCertificate is required when createNetworkLoadBalancer is true');
    }

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
      taskRole,
      runtimePlatform: {
        cpuArchitecture: props.cpuArchitecture ?? ecs.CpuArchitecture.ARM64,
        operatingSystemFamily: ecs.OperatingSystemFamily.LINUX,
      },
    });

    const logGroup = new logs.LogGroup(this, 'ServiceLogGroup', {
      retention: logs.RetentionDays.ONE_WEEK,
      removalPolicy: props.removalPolicy,
    });

    // Give the task role permission to describe the log group, create log streams in that group, and write log events to those streams
    logGroup.grant(taskRole, 'logs:DescribeLogGroups', 'logs:CreateLogStream', 'logs:PutLogEvents');

    const routerContainer = taskDefinition.addContainer('LambdaDispatchRouter', {
      image:
        props.routerImage ??
        ecs.ContainerImage.fromRegistry('public.ecr.aws/pwrdrvr/lambda-dispatch-router:latest'),
      logging: ecs.LogDriver.awsLogs({
        logGroup: logGroup,
        streamPrefix: 'router',
      }),
      environment: {
        DOTNET_ThreadPool_UnfairSemaphoreSpinLimit: '0',
        LAMBDA_DISPATCH_MinWorkerThreads: '1',
        LAMBDA_DISPATCH_MaxWorkerThreads: '4',
        LAMBDA_DISPATCH_FunctionName: props.lambdaFunction.functionArn,
        LAMBDA_DISPATCH_MaxConcurrentCount: '10',
        LAMBDA_DISPATCH_AllowInsecureControlChannel: 'true',
        LAMBDA_DISPATCH_PreferredControlChannelScheme: 'http',
        AWS_CLOUDWATCH_LOG_GROUP: logGroup.logGroupName,
      },
    });
    routerContainer.addPortMappings(
      { containerPort: 5001, protocol: ecs.Protocol.TCP },
      { containerPort: 5003, protocol: ecs.Protocol.TCP },
      { containerPort: 5004, protocol: ecs.Protocol.TCP },
    );

    const demoAppContainer = taskDefinition.addContainer('DemoApp', {
      image:
        props.demoAppImage ??
        ecs.ContainerImage.fromRegistry('public.ecr.aws/pwrdrvr/lambda-dispatch-demo-app:latest'),
      logging: ecs.LogDriver.awsLogs({
        logGroup: logGroup,
        streamPrefix: 'demo-app',
      }),
    });
    demoAppContainer.addPortMappings({ containerPort: 3001, protocol: ecs.Protocol.TCP });

    this.securityGroup = new ec2.SecurityGroup(this, 'EcsSecurityGroup', {
      vpc: props.vpc,
      allowAllOutbound: true,
      description: 'Security Group for ECS Fargate tasks',
    });

    // Add inbound rules for ports 5003 and 5004 on the router from within the VPC
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

    // Create target groups first
    this.targetGroupRouter = new elbv2.ApplicationTargetGroup(this, 'FargateTargetGroup', {
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
      deregistrationDelay: cdk.Duration.seconds(60),
    });

    this.targetGroupDemoApp = new elbv2.ApplicationTargetGroup(this, 'DemoAppTargetGroup', {
      vpc: props.vpc,
      port: 3001,
      protocol: elbv2.ApplicationProtocol.HTTP,
      targetType: elbv2.TargetType.IP,
      healthCheck: {
        path: '/health-quick',
        interval: cdk.Duration.seconds(5),
        timeout: cdk.Duration.seconds(2),
        healthyThresholdCount: 2,
      },
      deregistrationDelay: cdk.Duration.seconds(30),
    });

    // Configure capacity provider strategy based on props
    const capacityProviderStrategies = props.useFargateSpot
      ? [{ capacityProvider: 'FARGATE_SPOT', weight: 1 }]
      : [{ capacityProvider: 'FARGATE', weight: 1 }];

    // Create service
    this.service = new ecs.FargateService(this, 'EcsService', {
      cluster: props.cluster,
      taskDefinition,
      desiredCount: props.minCapacity ?? 1,
      assignPublicIp: false,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [this.securityGroup],
      capacityProviderStrategies,
    });

    // Add target group attachments with specific container names and ports
    this.targetGroupRouter.addTarget(
      this.service.loadBalancerTarget({
        containerName: 'LambdaDispatchRouter',
        containerPort: 5001,
      }),
    );

    this.targetGroupDemoApp.addTarget(
      this.service.loadBalancerTarget({
        containerName: 'DemoApp',
        containerPort: 3001,
      }),
    );

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

    // Create Network Load Balancer if requested
    if (
      props.createNetworkLoadBalancer &&
      props.networkLoadBalancer &&
      props.nlbCertificate &&
      props.nlbPorts
    ) {
      // Create HTTPS listener for Router (default port 443)
      const routerPort = props.nlbPorts?.routerPort ?? 443;
      const nlbRouterListener = props.networkLoadBalancer.addListener('NlbRouterHttpsListener', {
        port: routerPort,
        protocol: elbv2.Protocol.TLS,
        alpnPolicy: elbv2.AlpnPolicy.HTTP2_OPTIONAL,
        certificates: [props.nlbCertificate],
        sslPolicy: elbv2.SslPolicy.RECOMMENDED_TLS,
      });

      nlbRouterListener.addTargets('RouterNlbTarget', {
        port: 5001,
        protocol: elbv2.Protocol.TCP,
        targets: [this.service],
        deregistrationDelay: cdk.Duration.seconds(30),
      });

      // Create HTTPS listener for DemoApp if port is specified
      if (props.nlbPorts?.demoAppPort) {
        const nlbDempAppListener = props.networkLoadBalancer.addListener(
          'NlbDemoAppHttpsListener',
          {
            port: props.nlbPorts.demoAppPort,
            protocol: elbv2.Protocol.TLS,
            alpnPolicy: elbv2.AlpnPolicy.HTTP2_OPTIONAL,
            certificates: [props.nlbCertificate],
            sslPolicy: elbv2.SslPolicy.RECOMMENDED_TLS,
          },
        );

        nlbDempAppListener.addTargets('DemoAppNlbTarget', {
          port: 3001,
          protocol: elbv2.Protocol.TCP,
          targets: [this.service],
          deregistrationDelay: cdk.Duration.seconds(30),
        });
      }
    }
  }
}
