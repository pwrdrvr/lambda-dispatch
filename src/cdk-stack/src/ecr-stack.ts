import * as cdk from 'aws-cdk-lib';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import { Construct } from 'constructs';

export interface EcrStackProps extends cdk.StackProps {
  /**
   * Removal policy for the ECR repositories
   * @default undefined
   */
  readonly removalPolicy?: cdk.RemovalPolicy;
}

export class EcrStack extends cdk.Stack {
  public readonly lambdaRepo: ecr.Repository;
  public readonly ecsRepo: ecr.Repository;

  constructor(scope: Construct, id: string, props: EcrStackProps) {
    super(scope, id, props);

    this.lambdaRepo = new ecr.Repository(this, 'LambdaRepository', {
      repositoryName: 'lambda-dispatch-demo-app',
      removalPolicy: props?.removalPolicy,
    });

    this.ecsRepo = new ecr.Repository(this, 'EcsRepository', {
      repositoryName: 'lambda-dispatch-router',
      removalPolicy: props?.removalPolicy,
    });

    new cdk.CfnOutput(this, 'LambdaRepoName', {
      value: this.lambdaRepo.repositoryName,
      exportName: 'LambdaRepoName',
    });

    new cdk.CfnOutput(this, 'EcsRepoName', {
      value: this.ecsRepo.repositoryName,
      exportName: 'EcsRepoName',
    });
  }
}
