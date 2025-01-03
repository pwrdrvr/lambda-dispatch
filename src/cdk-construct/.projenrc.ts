import { awscdk, javascript } from 'projen';
const project = new awscdk.AwsCdkConstructLibrary({
  author: 'PwrDrvr LLC',
  authorAddress: 'harold@pwrdrvr.com',
  cdkVersion: '2.130.0',
  defaultReleaseBranch: 'main',
  description: 'CDK construct for setting up lambda-dispatch ECS service',
  license: 'MIT',
  jsiiVersion: '~5.5.0',
  name: '@pwrdrvr/lambda-dispatch-cdk',
  projenrcTs: true,
  repositoryUrl: 'https://github.com/pwrdrvr/lambda-dispatch.git',
  npmAccess: javascript.NpmAccess.PUBLIC,

  // deps: [],                /* Runtime dependencies of this module. */
  // description: undefined,  /* The description is just a string that helps people understand the purpose of the package. */
  // devDeps: [],             /* Build dependencies for this module. */
  // packageName: undefined,  /* The "name" in package.json. */

  publishToMaven: {
    mavenArtifactId: 'lambdadispatch-cdk',
    javaPackage: 'com.pwrdrvr.lambdadispatch.cdk',
    mavenGroupId: 'com.pwrdrvr.lambdadispatch',
  },
  publishToNuget: {
    dotNetNamespace: 'PwrDrvr.LambdaDispatch.CDK',
    packageId: 'PwrDrvr.LambdaDispatch.CDK',
  },
  publishToPypi: {
    distName: 'pwrdrvr.lambdadispatch.cdk',
    module: 'pwrdrvr.lambdadispatch.cdk',
  },
});

project.compileTask.exec('cp DockerfileLambda lib/DockerfileLambda');

project.synth();
