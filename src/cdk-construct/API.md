# API Reference <a name="API Reference" id="api-reference"></a>

## Constructs <a name="Constructs" id="Constructs"></a>

### EcsConstruct <a name="EcsConstruct" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct"></a>

#### Initializers <a name="Initializers" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.Initializer"></a>

```typescript
import { EcsConstruct } from '@pwrdrvr/lambda-dispatch-construct'

new EcsConstruct(scope: Construct, id: string, props: EcsConstructProps)
```

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstruct.Initializer.parameter.scope">scope</a></code> | <code>constructs.Construct</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstruct.Initializer.parameter.id">id</a></code> | <code>string</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstruct.Initializer.parameter.props">props</a></code> | <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstructProps">EcsConstructProps</a></code> | *No description.* |

---

##### `scope`<sup>Required</sup> <a name="scope" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.Initializer.parameter.scope"></a>

- *Type:* constructs.Construct

---

##### `id`<sup>Required</sup> <a name="id" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.Initializer.parameter.id"></a>

- *Type:* string

---

##### `props`<sup>Required</sup> <a name="props" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.Initializer.parameter.props"></a>

- *Type:* <a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstructProps">EcsConstructProps</a>

---

#### Methods <a name="Methods" id="Methods"></a>

| **Name** | **Description** |
| --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstruct.toString">toString</a></code> | Returns a string representation of this construct. |

---

##### `toString` <a name="toString" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.toString"></a>

```typescript
public toString(): string
```

Returns a string representation of this construct.

#### Static Functions <a name="Static Functions" id="Static Functions"></a>

| **Name** | **Description** |
| --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstruct.isConstruct">isConstruct</a></code> | Checks if `x` is a construct. |

---

##### `isConstruct` <a name="isConstruct" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.isConstruct"></a>

```typescript
import { EcsConstruct } from '@pwrdrvr/lambda-dispatch-construct'

EcsConstruct.isConstruct(x: any)
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

###### `x`<sup>Required</sup> <a name="x" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.isConstruct.parameter.x"></a>

- *Type:* any

Any object.

---

#### Properties <a name="Properties" id="Properties"></a>

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstruct.property.node">node</a></code> | <code>constructs.Node</code> | The tree node. |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstruct.property.securityGroup">securityGroup</a></code> | <code>aws-cdk-lib.aws_ec2.SecurityGroup</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstruct.property.service">service</a></code> | <code>aws-cdk-lib.aws_ecs.FargateService</code> | *No description.* |

---

##### `node`<sup>Required</sup> <a name="node" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.property.node"></a>

```typescript
public readonly node: Node;
```

- *Type:* constructs.Node

The tree node.

---

##### `securityGroup`<sup>Required</sup> <a name="securityGroup" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.property.securityGroup"></a>

```typescript
public readonly securityGroup: SecurityGroup;
```

- *Type:* aws-cdk-lib.aws_ec2.SecurityGroup

---

##### `service`<sup>Required</sup> <a name="service" id="@pwrdrvr/lambda-dispatch-construct.EcsConstruct.property.service"></a>

```typescript
public readonly service: FargateService;
```

- *Type:* aws-cdk-lib.aws_ecs.FargateService

---


### LambdaConstruct <a name="LambdaConstruct" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct"></a>

#### Initializers <a name="Initializers" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.Initializer"></a>

```typescript
import { LambdaConstruct } from '@pwrdrvr/lambda-dispatch-construct'

new LambdaConstruct(scope: Construct, id: string, props: LambdaConstructProps)
```

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.Initializer.parameter.scope">scope</a></code> | <code>constructs.Construct</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.Initializer.parameter.id">id</a></code> | <code>string</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.Initializer.parameter.props">props</a></code> | <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstructProps">LambdaConstructProps</a></code> | *No description.* |

---

##### `scope`<sup>Required</sup> <a name="scope" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.Initializer.parameter.scope"></a>

- *Type:* constructs.Construct

---

##### `id`<sup>Required</sup> <a name="id" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.Initializer.parameter.id"></a>

- *Type:* string

---

##### `props`<sup>Required</sup> <a name="props" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.Initializer.parameter.props"></a>

- *Type:* <a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstructProps">LambdaConstructProps</a>

---

#### Methods <a name="Methods" id="Methods"></a>

| **Name** | **Description** |
| --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.toString">toString</a></code> | Returns a string representation of this construct. |

---

##### `toString` <a name="toString" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.toString"></a>

```typescript
public toString(): string
```

Returns a string representation of this construct.

#### Static Functions <a name="Static Functions" id="Static Functions"></a>

| **Name** | **Description** |
| --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.isConstruct">isConstruct</a></code> | Checks if `x` is a construct. |

---

##### `isConstruct` <a name="isConstruct" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.isConstruct"></a>

```typescript
import { LambdaConstruct } from '@pwrdrvr/lambda-dispatch-construct'

LambdaConstruct.isConstruct(x: any)
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

###### `x`<sup>Required</sup> <a name="x" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.isConstruct.parameter.x"></a>

- *Type:* any

Any object.

---

#### Properties <a name="Properties" id="Properties"></a>

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.property.node">node</a></code> | <code>constructs.Node</code> | The tree node. |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.property.function">function</a></code> | <code>aws-cdk-lib.aws_lambda.Function</code> | *No description.* |

---

##### `node`<sup>Required</sup> <a name="node" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.property.node"></a>

```typescript
public readonly node: Node;
```

- *Type:* constructs.Node

The tree node.

---

##### `function`<sup>Required</sup> <a name="function" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstruct.property.function"></a>

```typescript
public readonly function: Function;
```

- *Type:* aws-cdk-lib.aws_lambda.Function

---


## Structs <a name="Structs" id="Structs"></a>

### EcsConstructProps <a name="EcsConstructProps" id="@pwrdrvr/lambda-dispatch-construct.EcsConstructProps"></a>

#### Initializer <a name="Initializer" id="@pwrdrvr/lambda-dispatch-construct.EcsConstructProps.Initializer"></a>

```typescript
import { EcsConstructProps } from '@pwrdrvr/lambda-dispatch-construct'

const ecsConstructProps: EcsConstructProps = { ... }
```

#### Properties <a name="Properties" id="Properties"></a>

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstructProps.property.lambdaFunction">lambdaFunction</a></code> | <code>aws-cdk-lib.aws_lambda.IFunction</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.EcsConstructProps.property.vpc">vpc</a></code> | <code>aws-cdk-lib.aws_ec2.IVpc</code> | *No description.* |

---

##### `lambdaFunction`<sup>Required</sup> <a name="lambdaFunction" id="@pwrdrvr/lambda-dispatch-construct.EcsConstructProps.property.lambdaFunction"></a>

```typescript
public readonly lambdaFunction: IFunction;
```

- *Type:* aws-cdk-lib.aws_lambda.IFunction

---

##### `vpc`<sup>Required</sup> <a name="vpc" id="@pwrdrvr/lambda-dispatch-construct.EcsConstructProps.property.vpc"></a>

```typescript
public readonly vpc: IVpc;
```

- *Type:* aws-cdk-lib.aws_ec2.IVpc

---

### LambdaConstructProps <a name="LambdaConstructProps" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstructProps"></a>

#### Initializer <a name="Initializer" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstructProps.Initializer"></a>

```typescript
import { LambdaConstructProps } from '@pwrdrvr/lambda-dispatch-construct'

const lambdaConstructProps: LambdaConstructProps = { ... }
```

#### Properties <a name="Properties" id="Properties"></a>

| **Name** | **Type** | **Description** |
| --- | --- | --- |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstructProps.property.vpc">vpc</a></code> | <code>aws-cdk-lib.aws_ec2.IVpc</code> | *No description.* |
| <code><a href="#@pwrdrvr/lambda-dispatch-construct.LambdaConstructProps.property.ecsSecurityGroup">ecsSecurityGroup</a></code> | <code>aws-cdk-lib.aws_ec2.ISecurityGroup</code> | *No description.* |

---

##### `vpc`<sup>Required</sup> <a name="vpc" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstructProps.property.vpc"></a>

```typescript
public readonly vpc: IVpc;
```

- *Type:* aws-cdk-lib.aws_ec2.IVpc

---

##### `ecsSecurityGroup`<sup>Optional</sup> <a name="ecsSecurityGroup" id="@pwrdrvr/lambda-dispatch-construct.LambdaConstructProps.property.ecsSecurityGroup"></a>

```typescript
public readonly ecsSecurityGroup: ISecurityGroup;
```

- *Type:* aws-cdk-lib.aws_ec2.ISecurityGroup

---



