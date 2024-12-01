import { awscdk } from 'projen';
const project = new awscdk.AwsCdkConstructLibrary({
  author: 'Harold Hunt',
  authorAddress: 'harold@pwrdrvr.com',
  cdkVersion: '2.130.0',
  defaultReleaseBranch: 'main',
  jsiiVersion: '~5.5.0',
  name: '@pwrdrvr/lambda-dispatch-construct',
  projenrcTs: true,
  repositoryUrl: 'https://github.com/harold/cdk-construct.git',

  // deps: [],                /* Runtime dependencies of this module. */
  // description: undefined,  /* The description is just a string that helps people understand the purpose of the package. */
  // devDeps: [],             /* Build dependencies for this module. */
  // packageName: undefined,  /* The "name" in package.json. */
});

project.compileTask.exec('cp DockerfileLambda lib/DockerfileLambda');

project.synth();
