import * as cdk from 'aws-cdk-lib';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import { Construct } from 'constructs';

export class EcrStack extends cdk.Stack {
  public readonly lambdaRepo: ecr.Repository;
  public readonly ecsRepo: ecr.Repository;

  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    this.lambdaRepo = new ecr.Repository(this, 'LambdaRepository', {
      repositoryName: 'lambda-dispatch-demo-app',
    });

    this.ecsRepo = new ecr.Repository(this, 'EcsRepository', {
      repositoryName: 'lambda-dispatch-router',
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
