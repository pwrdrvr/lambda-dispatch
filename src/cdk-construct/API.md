# API Reference <a name="API Reference" id="api-reference"></a>

## Constructs <a name="Constructs" id="Constructs"></a>

### LambdaDispatchECS <a name="LambdaDispatchECS" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS"></a>

Creates an ECS service with the necessary configuration for Lambda Dispatch.

#### Initializers <a name="Initializers" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.Initializer"></a>

```typescript
import { LambdaDispatchECS } from '@pwrdrvr/lambda-dispatch-cdk'

new LambdaDispatchECS(scope: Construct, id: string, props: LambdaDispatchECSProps)
```

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.Initializer.parameter.scope">scope</a></code> | <code>constructs.Construct</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.Initializer.parameter.id">id</a></code> | <code>string</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.Initializer.parameter.props">props</a></code> | <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps">LambdaDispatchECSProps</a></code> | *No description.* |

---

##### `scope`<sup>Required</sup> <a name="scope" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.Initializer.parameter.scope"></a>

- *Type:* constructs.Construct

---

##### `id`<sup>Required</sup> <a name="id" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.Initializer.parameter.id"></a>

- *Type:* string

---

##### `props`<sup>Required</sup> <a name="props" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.Initializer.parameter.props"></a>

- *Type:* <a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps">LambdaDispatchECSProps</a>

---

#### Methods <a name="Methods" id="Methods"></a>

| **Name** | **Description** |
| --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.toString">toString</a></code> | Returns a string representation of this construct. |

---

##### `toString` <a name="toString" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.toString"></a>

```typescript
public toString(): string
```

Returns a string representation of this construct.

#### Static Functions <a name="Static Functions" id="Static Functions"></a>

| **Name** | **Description** |
| --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.isConstruct">isConstruct</a></code> | Checks if `x` is a construct. |

---

##### `isConstruct` <a name="isConstruct" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.isConstruct"></a>

```typescript
import { LambdaDispatchECS } from '@pwrdrvr/lambda-dispatch-cdk'

LambdaDispatchECS.isConstruct(x: any)
```

Checks if `x` is a construct.

Use this method instead of `instanceof` to properly detect `Construct`
instances, even when the construct library is symlinked.

Explanation: in JavaScript, multiple copies of the `constructs` library on
disk are seen as independent, completely different libraries. As a
consequence, the class `Construct` in each copy of the `constructs` library
is seen as a different class, and an instance of one class will not test as
`instanceof` the other class. `npm install` will not create installations
like this, but users may manually symlink construct libraries together or
use a monorepo tool: in those cases, multiple copies of the `constructs`
library can be accidentally installed, and `instanceof` will behave
unpredictably. It is safest to avoid using `instanceof`, and using
this type-testing method instead.

###### `x`<sup>Required</sup> <a name="x" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.isConstruct.parameter.x"></a>

- *Type:* any

Any object.

---

#### Properties <a name="Properties" id="Properties"></a>

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.node">node</a></code> | <code>constructs.Node</code> | The tree node. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.securityGroup">securityGroup</a></code> | <code>aws-cdk-lib.aws_ec2.SecurityGroup</code> | Security group for the ECS tasks. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.service">service</a></code> | <code>aws-cdk-lib.aws_ecs.FargateService</code> | The ECS Fargate service. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.targetGroupDemoApp">targetGroupDemoApp</a></code> | <code>aws-cdk-lib.aws_elasticloadbalancingv2.ApplicationTargetGroup</code> | Application Load Balancer target group for the Demo App service. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.targetGroupRouter">targetGroupRouter</a></code> | <code>aws-cdk-lib.aws_elasticloadbalancingv2.ApplicationTargetGroup</code> | Application Load Balancer target group for the Router service. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.nlbDemoAppListener">nlbDemoAppListener</a></code> | <code>aws-cdk-lib.aws_elasticloadbalancingv2.NetworkListener</code> | Network Load Balancer Demo App Listener Only set if createNetworkLoadBalancer is true and demoAppPort is specified. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.nlbRouterListener">nlbRouterListener</a></code> | <code>aws-cdk-lib.aws_elasticloadbalancingv2.NetworkListener</code> | Network Load Balancer Router Listener Only set if createNetworkLoadBalancer is true. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.nlbTargetGroupDemoApp">nlbTargetGroupDemoApp</a></code> | <code>aws-cdk-lib.aws_elasticloadbalancingv2.NetworkTargetGroup</code> | Network Load Balancer target group for the Demo App service Only set if createNetworkLoadBalancer is true. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.nlbTargetGroupRouter">nlbTargetGroupRouter</a></code> | <code>aws-cdk-lib.aws_elasticloadbalancingv2.NetworkTargetGroup</code> | Network Load Balancer target group for the Router service Only set if createNetworkLoadBalancer is true. |

---

##### `node`<sup>Required</sup> <a name="node" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.node"></a>

```typescript
public readonly node: Node;
```

- *Type:* constructs.Node

The tree node.

---

##### `securityGroup`<sup>Required</sup> <a name="securityGroup" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.securityGroup"></a>

```typescript
public readonly securityGroup: SecurityGroup;
```

- *Type:* aws-cdk-lib.aws_ec2.SecurityGroup

Security group for the ECS tasks.

---

##### `service`<sup>Required</sup> <a name="service" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.service"></a>

```typescript
public readonly service: FargateService;
```

- *Type:* aws-cdk-lib.aws_ecs.FargateService

The ECS Fargate service.

---

##### `targetGroupDemoApp`<sup>Required</sup> <a name="targetGroupDemoApp" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.targetGroupDemoApp"></a>

```typescript
public readonly targetGroupDemoApp: ApplicationTargetGroup;
```

- *Type:* aws-cdk-lib.aws_elasticloadbalancingv2.ApplicationTargetGroup

Application Load Balancer target group for the Demo App service.

---

##### `targetGroupRouter`<sup>Required</sup> <a name="targetGroupRouter" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.targetGroupRouter"></a>

```typescript
public readonly targetGroupRouter: ApplicationTargetGroup;
```

- *Type:* aws-cdk-lib.aws_elasticloadbalancingv2.ApplicationTargetGroup

Application Load Balancer target group for the Router service.

---

##### `nlbDemoAppListener`<sup>Optional</sup> <a name="nlbDemoAppListener" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.nlbDemoAppListener"></a>

```typescript
public readonly nlbDemoAppListener: NetworkListener;
```

- *Type:* aws-cdk-lib.aws_elasticloadbalancingv2.NetworkListener

Network Load Balancer Demo App Listener Only set if createNetworkLoadBalancer is true and demoAppPort is specified.

---

##### `nlbRouterListener`<sup>Optional</sup> <a name="nlbRouterListener" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.nlbRouterListener"></a>

```typescript
public readonly nlbRouterListener: NetworkListener;
```

- *Type:* aws-cdk-lib.aws_elasticloadbalancingv2.NetworkListener

Network Load Balancer Router Listener Only set if createNetworkLoadBalancer is true.

---

##### `nlbTargetGroupDemoApp`<sup>Optional</sup> <a name="nlbTargetGroupDemoApp" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.nlbTargetGroupDemoApp"></a>

```typescript
public readonly nlbTargetGroupDemoApp: NetworkTargetGroup;
```

- *Type:* aws-cdk-lib.aws_elasticloadbalancingv2.NetworkTargetGroup

Network Load Balancer target group for the Demo App service Only set if createNetworkLoadBalancer is true.

---

##### `nlbTargetGroupRouter`<sup>Optional</sup> <a name="nlbTargetGroupRouter" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECS.property.nlbTargetGroupRouter"></a>

```typescript
public readonly nlbTargetGroupRouter: NetworkTargetGroup;
```

- *Type:* aws-cdk-lib.aws_elasticloadbalancingv2.NetworkTargetGroup

Network Load Balancer target group for the Router service Only set if createNetworkLoadBalancer is true.

---


### LambdaDispatchFunction <a name="LambdaDispatchFunction" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction"></a>

Creates a Lambda function with the necessary configuration for Lambda Dispatch.

#### Initializers <a name="Initializers" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.Initializer"></a>

```typescript
import { LambdaDispatchFunction } from '@pwrdrvr/lambda-dispatch-cdk'

new LambdaDispatchFunction(scope: Construct, id: string, props: LambdaDispatchFunctionProps)
```

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.Initializer.parameter.scope">scope</a></code> | <code>constructs.Construct</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.Initializer.parameter.id">id</a></code> | <code>string</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.Initializer.parameter.props">props</a></code> | <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps">LambdaDispatchFunctionProps</a></code> | *No description.* |

---

##### `scope`<sup>Required</sup> <a name="scope" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.Initializer.parameter.scope"></a>

- *Type:* constructs.Construct

---

##### `id`<sup>Required</sup> <a name="id" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.Initializer.parameter.id"></a>

- *Type:* string

---

##### `props`<sup>Required</sup> <a name="props" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.Initializer.parameter.props"></a>

- *Type:* <a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps">LambdaDispatchFunctionProps</a>

---

#### Methods <a name="Methods" id="Methods"></a>

| **Name** | **Description** |
| --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.toString">toString</a></code> | Returns a string representation of this construct. |

---

##### `toString` <a name="toString" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.toString"></a>

```typescript
public toString(): string
```

Returns a string representation of this construct.

#### Static Functions <a name="Static Functions" id="Static Functions"></a>

| **Name** | **Description** |
| --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.isConstruct">isConstruct</a></code> | Checks if `x` is a construct. |

---

##### `isConstruct` <a name="isConstruct" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.isConstruct"></a>

```typescript
import { LambdaDispatchFunction } from '@pwrdrvr/lambda-dispatch-cdk'

LambdaDispatchFunction.isConstruct(x: any)
```

Checks if `x` is a construct.

Use this method instead of `instanceof` to properly detect `Construct`
instances, even when the construct library is symlinked.

Explanation: in JavaScript, multiple copies of the `constructs` library on
disk are seen as independent, completely different libraries. As a
consequence, the class `Construct` in each copy of the `constructs` library
is seen as a different class, and an instance of one class will not test as
`instanceof` the other class. `npm install` will not create installations
like this, but users may manually symlink construct libraries together or
use a monorepo tool: in those cases, multiple copies of the `constructs`
library can be accidentally installed, and `instanceof` will behave
unpredictably. It is safest to avoid using `instanceof`, and using
this type-testing method instead.

###### `x`<sup>Required</sup> <a name="x" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.isConstruct.parameter.x"></a>

- *Type:* any

Any object.

---

#### Properties <a name="Properties" id="Properties"></a>

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.property.node">node</a></code> | <code>constructs.Node</code> | The tree node. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.property.function">function</a></code> | <code>aws-cdk-lib.aws_lambda.Function</code> | The Lambda function instance. |

---

##### `node`<sup>Required</sup> <a name="node" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.property.node"></a>

```typescript
public readonly node: Node;
```

- *Type:* constructs.Node

The tree node.

---

##### `function`<sup>Required</sup> <a name="function" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunction.property.function"></a>

```typescript
public readonly function: Function;
```

- *Type:* aws-cdk-lib.aws_lambda.Function

The Lambda function instance.

---


## Structs <a name="Structs" id="Structs"></a>

### LambdaDispatchECSNlbPortConfiguration <a name="LambdaDispatchECSNlbPortConfiguration" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSNlbPortConfiguration"></a>

Port configuration for the Network Load Balancer.

#### Initializer <a name="Initializer" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSNlbPortConfiguration.Initializer"></a>

```typescript
import { LambdaDispatchECSNlbPortConfiguration } from '@pwrdrvr/lambda-dispatch-cdk'

const lambdaDispatchECSNlbPortConfiguration: LambdaDispatchECSNlbPortConfiguration = { ... }
```

#### Properties <a name="Properties" id="Properties"></a>

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSNlbPortConfiguration.property.demoAppPort">demoAppPort</a></code> | <code>number</code> | Port that the NLB will listen on for the Demo App container. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSNlbPortConfiguration.property.routerPort">routerPort</a></code> | <code>number</code> | Port that the NLB will listen on for the Router container. |

---

##### `demoAppPort`<sup>Required</sup> <a name="demoAppPort" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSNlbPortConfiguration.property.demoAppPort"></a>

```typescript
public readonly demoAppPort: number;
```

- *Type:* number
- *Default:* undefined

Port that the NLB will listen on for the Demo App container.

---

##### `routerPort`<sup>Required</sup> <a name="routerPort" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSNlbPortConfiguration.property.routerPort"></a>

```typescript
public readonly routerPort: number;
```

- *Type:* number
- *Default:* 443

Port that the NLB will listen on for the Router container.

---

### LambdaDispatchECSProps <a name="LambdaDispatchECSProps" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps"></a>

Properties for the ECS construct.

#### Initializer <a name="Initializer" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.Initializer"></a>

```typescript
import { LambdaDispatchECSProps } from '@pwrdrvr/lambda-dispatch-cdk'

const lambdaDispatchECSProps: LambdaDispatchECSProps = { ... }
```

#### Properties <a name="Properties" id="Properties"></a>

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.cluster">cluster</a></code> | <code>aws-cdk-lib.aws_ecs.ICluster</code> | ECS Cluster. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.lambdaFunction">lambdaFunction</a></code> | <code>aws-cdk-lib.aws_lambda.IFunction</code> | Lambda function that will be invoked by the ECS service. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.vpc">vpc</a></code> | <code>aws-cdk-lib.aws_ec2.IVpc</code> | VPC where the ECS service will be deployed. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.cpu">cpu</a></code> | <code>number</code> | CPU units for the ECS task. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.cpuArchitecture">cpuArchitecture</a></code> | <code>aws-cdk-lib.aws_ecs.CpuArchitecture</code> | CPU architecture to use for the ECS tasks Note: Fargate Spot only supports AMD64 architecture. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.createNetworkLoadBalancer">createNetworkLoadBalancer</a></code> | <code>boolean</code> | Whether to create a Network Load Balancer in addition to the Application Load Balancer. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.demoAppImage">demoAppImage</a></code> | <code>aws-cdk-lib.aws_ecs.ContainerImage</code> | Image for the demo app This can be the same as the lambda image because it just won't start the lambda extension. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.maxCapacity">maxCapacity</a></code> | <code>number</code> | Maximum number of ECS tasks. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.memoryLimitMiB">memoryLimitMiB</a></code> | <code>number</code> | Memory limit for the ECS task in MiB. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.minCapacity">minCapacity</a></code> | <code>number</code> | Minimum number of ECS tasks. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.networkLoadBalancer">networkLoadBalancer</a></code> | <code>aws-cdk-lib.aws_elasticloadbalancingv2.INetworkLoadBalancer</code> | Network Load Balancer. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.nlbCertificate">nlbCertificate</a></code> | <code>aws-cdk-lib.aws_elasticloadbalancingv2.IListenerCertificate</code> | Certificate to use for HTTPS listeners on the Network Load Balancer Required if createNetworkLoadBalancer is true. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.nlbPorts">nlbPorts</a></code> | <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSNlbPortConfiguration">LambdaDispatchECSNlbPortConfiguration</a></code> | Port configuration for the Network Load Balancer These are the ports that the NLB will listen on and forward to the containers. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.removalPolicy">removalPolicy</a></code> | <code>aws-cdk-lib.RemovalPolicy</code> | The removal policy to apply to the log group. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.routerImage">routerImage</a></code> | <code>aws-cdk-lib.aws_ecs.ContainerImage</code> | Image for the lambda-dispatch Router. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.useFargateSpot">useFargateSpot</a></code> | <code>boolean</code> | Whether to use Fargate Spot capacity provider Note: Fargate Spot only supports AMD64 architecture. |

---

##### `cluster`<sup>Required</sup> <a name="cluster" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.cluster"></a>

```typescript
public readonly cluster: ICluster;
```

- *Type:* aws-cdk-lib.aws_ecs.ICluster

ECS Cluster.

---

##### `lambdaFunction`<sup>Required</sup> <a name="lambdaFunction" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.lambdaFunction"></a>

```typescript
public readonly lambdaFunction: IFunction;
```

- *Type:* aws-cdk-lib.aws_lambda.IFunction

Lambda function that will be invoked by the ECS service.

---

##### `vpc`<sup>Required</sup> <a name="vpc" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.vpc"></a>

```typescript
public readonly vpc: IVpc;
```

- *Type:* aws-cdk-lib.aws_ec2.IVpc

VPC where the ECS service will be deployed.

---

##### `cpu`<sup>Optional</sup> <a name="cpu" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.cpu"></a>

```typescript
public readonly cpu: number;
```

- *Type:* number
- *Default:* 1024

CPU units for the ECS task.

---

##### `cpuArchitecture`<sup>Optional</sup> <a name="cpuArchitecture" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.cpuArchitecture"></a>

```typescript
public readonly cpuArchitecture: CpuArchitecture;
```

- *Type:* aws-cdk-lib.aws_ecs.CpuArchitecture
- *Default:* ARM64

CPU architecture to use for the ECS tasks Note: Fargate Spot only supports AMD64 architecture.

---

##### `createNetworkLoadBalancer`<sup>Optional</sup> <a name="createNetworkLoadBalancer" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.createNetworkLoadBalancer"></a>

```typescript
public readonly createNetworkLoadBalancer: boolean;
```

- *Type:* boolean
- *Default:* false

Whether to create a Network Load Balancer in addition to the Application Load Balancer.

---

##### `demoAppImage`<sup>Optional</sup> <a name="demoAppImage" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.demoAppImage"></a>

```typescript
public readonly demoAppImage: ContainerImage;
```

- *Type:* aws-cdk-lib.aws_ecs.ContainerImage
- *Default:* undefined

Image for the demo app This can be the same as the lambda image because it just won't start the lambda extension.

---

##### `maxCapacity`<sup>Optional</sup> <a name="maxCapacity" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.maxCapacity"></a>

```typescript
public readonly maxCapacity: number;
```

- *Type:* number
- *Default:* 10

Maximum number of ECS tasks.

---

##### `memoryLimitMiB`<sup>Optional</sup> <a name="memoryLimitMiB" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.memoryLimitMiB"></a>

```typescript
public readonly memoryLimitMiB: number;
```

- *Type:* number
- *Default:* 2048

Memory limit for the ECS task in MiB.

---

##### `minCapacity`<sup>Optional</sup> <a name="minCapacity" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.minCapacity"></a>

```typescript
public readonly minCapacity: number;
```

- *Type:* number
- *Default:* 1

Minimum number of ECS tasks.

---

##### `networkLoadBalancer`<sup>Optional</sup> <a name="networkLoadBalancer" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.networkLoadBalancer"></a>

```typescript
public readonly networkLoadBalancer: INetworkLoadBalancer;
```

- *Type:* aws-cdk-lib.aws_elasticloadbalancingv2.INetworkLoadBalancer

Network Load Balancer.

---

##### `nlbCertificate`<sup>Optional</sup> <a name="nlbCertificate" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.nlbCertificate"></a>

```typescript
public readonly nlbCertificate: IListenerCertificate;
```

- *Type:* aws-cdk-lib.aws_elasticloadbalancingv2.IListenerCertificate

Certificate to use for HTTPS listeners on the Network Load Balancer Required if createNetworkLoadBalancer is true.

---

##### `nlbPorts`<sup>Optional</sup> <a name="nlbPorts" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.nlbPorts"></a>

```typescript
public readonly nlbPorts: LambdaDispatchECSNlbPortConfiguration;
```

- *Type:* <a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSNlbPortConfiguration">LambdaDispatchECSNlbPortConfiguration</a>
- *Default:* undefined

Port configuration for the Network Load Balancer These are the ports that the NLB will listen on and forward to the containers.

---

##### `removalPolicy`<sup>Optional</sup> <a name="removalPolicy" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.removalPolicy"></a>

```typescript
public readonly removalPolicy: RemovalPolicy;
```

- *Type:* aws-cdk-lib.RemovalPolicy
- *Default:* undefined

The removal policy to apply to the log group.

---

##### `routerImage`<sup>Optional</sup> <a name="routerImage" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.routerImage"></a>

```typescript
public readonly routerImage: ContainerImage;
```

- *Type:* aws-cdk-lib.aws_ecs.ContainerImage
- *Default:* latest image from public ECR repository

Image for the lambda-dispatch Router.

---

##### `useFargateSpot`<sup>Optional</sup> <a name="useFargateSpot" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchECSProps.property.useFargateSpot"></a>

```typescript
public readonly useFargateSpot: boolean;
```

- *Type:* boolean
- *Default:* false

Whether to use Fargate Spot capacity provider Note: Fargate Spot only supports AMD64 architecture.

---

### LambdaDispatchFunctionProps <a name="LambdaDispatchFunctionProps" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps"></a>

Properties for the Lambda construct.

#### Initializer <a name="Initializer" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.Initializer"></a>

```typescript
import { LambdaDispatchFunctionProps } from '@pwrdrvr/lambda-dispatch-cdk'

const lambdaDispatchFunctionProps: LambdaDispatchFunctionProps = { ... }
```

#### Properties <a name="Properties" id="Properties"></a>

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.vpc">vpc</a></code> | <code>aws-cdk-lib.aws_ec2.IVpc</code> | VPC where the Lambda function will be deployed. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.architecture">architecture</a></code> | <code>aws-cdk-lib.aws_lambda.Architecture</code> | CPU architecture for the Lambda function. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.dockerImage">dockerImage</a></code> | <code>aws-cdk-lib.aws_lambda.DockerImageCode</code> | Docker image for the Lambda function. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.ecsSecurityGroup">ecsSecurityGroup</a></code> | <code>aws-cdk-lib.aws_ec2.ISecurityGroup</code> | Optional security group for ECS tasks that will invoke this Lambda. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.memorySize">memorySize</a></code> | <code>number</code> | Memory size for the Lambda function in MB. |
| <code><a href="#@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.timeout">timeout</a></code> | <code>aws-cdk-lib.Duration</code> | Timeout for the Lambda function. |

---

##### `vpc`<sup>Required</sup> <a name="vpc" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.vpc"></a>

```typescript
public readonly vpc: IVpc;
```

- *Type:* aws-cdk-lib.aws_ec2.IVpc

VPC where the Lambda function will be deployed.

---

##### `architecture`<sup>Optional</sup> <a name="architecture" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.architecture"></a>

```typescript
public readonly architecture: Architecture;
```

- *Type:* aws-cdk-lib.aws_lambda.Architecture
- *Default:* ARM_64

CPU architecture for the Lambda function.

---

##### `dockerImage`<sup>Optional</sup> <a name="dockerImage" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.dockerImage"></a>

```typescript
public readonly dockerImage: DockerImageCode;
```

- *Type:* aws-cdk-lib.aws_lambda.DockerImageCode
- *Default:* latest image from public ECR repository

Docker image for the Lambda function.

---

##### `ecsSecurityGroup`<sup>Optional</sup> <a name="ecsSecurityGroup" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.ecsSecurityGroup"></a>

```typescript
public readonly ecsSecurityGroup: ISecurityGroup;
```

- *Type:* aws-cdk-lib.aws_ec2.ISecurityGroup

Optional security group for ECS tasks that will invoke this Lambda.

---

##### `memorySize`<sup>Optional</sup> <a name="memorySize" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.memorySize"></a>

```typescript
public readonly memorySize: number;
```

- *Type:* number
- *Default:* 192

Memory size for the Lambda function in MB.

---

##### `timeout`<sup>Optional</sup> <a name="timeout" id="@pwrdrvr/lambda-dispatch-cdk.LambdaDispatchFunctionProps.property.timeout"></a>

```typescript
public readonly timeout: Duration;
```

- *Type:* aws-cdk-lib.Duration
- *Default:* 60 seconds

Timeout for the Lambda function.

---



