import { awscdk } from 'projen';
const project = new awscdk.AwsCdkTypeScriptApp({
  cdkVersion: '2.130.0',
  defaultReleaseBranch: 'main',
  name: '@pwrdrvr/lambda-dispatch-cdk-stack',
  projenrcTs: true,
  repository: 'https://github.com/pwrdrvr/lambda-dispatch.git',

  deps: [
    'cdk-fck-nat@1.5.11',
    '@pwrdrvr/lambda-dispatch-cdk@*',
  ] /* Runtime dependencies of this module. */,
  // description: undefined,  /* The description is just a string that helps people understand the purpose of the package. */
  // devDeps: [],             /* Build dependencies for this module. */
  devDeps: [],
  // packageName: undefined,  /* The "name" in package.json. */
});
project.synth();
