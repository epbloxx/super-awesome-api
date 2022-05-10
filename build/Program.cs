using Amazon;
using Amazon.ECR;
using Amazon.ECR.Model;
using Build;
using Build.Extensions;
using Build.Tasks;
using Cake.CloudFormation;
using Cake.Common;
using Cake.Common.Build;
using Cake.Common.Diagnostics;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Docker;
using Cake.Frosting;
using Cake.VulnerabilityScanner;
using dotenv.net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class Program
{

    public static string GetEcrRepoName(this BuildContext context) => $"{context.Options.ApplicationPlatfrom}/{context.Options.ApplicationSystem}-{context.Options.ApplicationSubsystem}-{context.Options.ApplicationName}-repo";
    public static int Main(string[] args)
    {
        DotEnv.Fluent()
              .WithEnvFiles("../.env")
              .Load();

        return new CakeHost()
            .UseWorkingDirectory("..")
            .InstallTools()
            .UseContext<BuildContext>()
            .Run(args);
    }
}
[TaskName("scan pacakges")]
public sealed class ScanPackagesTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        // SonaType token,  base64 username:password
        var ossIndexToken = context.Environment.GetEnvironmentVariable("OSS_INDEX_TOKEN");
        await context.ScanPackagesAsync(new ScanPackagesSettings
        {
            Ecosystem = "nuget",
            FailOnVulnerability = true,
            OssIndexBaseUrl = "https://ossindex.sonatype.org/",
            OssIndexToken = ossIndexToken,
            SolutionFile = context.Options.SolutionFile,
            Verbosity = Microsoft.Extensions.Logging.LogLevel.Debug,

        }, CancellationToken.None);
    }
}

[TaskName("deploy-ecr")]
public class EcrDeployBuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
     if (context.IsReleasableBranch())
        {
    
        if (context.TeamCity().IsRunningOnTeamCity)
        {
            System.Environment.SetEnvironmentVariable("AWS_PROFILE", null);
            context.Information("Set AWS_PROFILE to null");
        }

        context.Log.Information("deploying ECR..");
        string stackName = @$"{context.Options.ApplicationPlatfrom}-{context.Options.ApplicationSystem}-{context.Options.ApplicationSubsystem}-{context.Options.ApplicationName}-Ecr-stack"
                .Replace("_", "-")
                .ToLower();
        context.CloudFormationDeploy(new DeployArguments
        {
            StackName = stackName,
            TemplateFile = "template-ecr.yaml",
            ParameterOverrides = new Dictionary<string, string>
            {{"Environment", context.Options.ApplicationEnvironment}},
            Capabilities = new List<string> { "CAPABILITY_IAM", "CAPABILITY_NAMED_IAM", "CAPABILITY_AUTO_EXPAND" }
        });
        context.Information("Successfully deploy ECR stack ");
        }
    }
}

[TaskName("docker-build-and-push")]
public class DockerBuildAndPushTask : AsyncFrostingTask<BuildContext>
{
    private const string MyGetApiKeyKey = "MyGetApiKey";

    public override async Task RunAsync(BuildContext context)
    {
        if (context.IsReleasableBranch())
        {
            context.Log.Information("building and tagging image..");

            string destinationTag = $"{context.Options.EcrRoot}/{context.GetEcrRepoName()}:{context.Options.ApplicationVersion}".ToLower();

            var region = RegionEndpoint.GetBySystemName(context.Options.AwsRegion);

            var ecrClient = new AmazonECRClient(region);
            var getTokenResponse = await ecrClient.GetAuthorizationTokenAsync(new GetAuthorizationTokenRequest());
            if (getTokenResponse.HttpStatusCode == HttpStatusCode.OK)
            {
                var authTokenBytes = Convert.FromBase64String(getTokenResponse.AuthorizationData[0].AuthorizationToken);
                var authToken = Encoding.UTF8.GetString(authTokenBytes);
                var decodedTokens = authToken.Split(':');

                context.DockerLogin(decodedTokens[0], decodedTokens[1], getTokenResponse.AuthorizationData.FirstOrDefault()?.ProxyEndpoint);

                context.Information($"Image with tag : {destinationTag} ");

                context.DockerBuild(new DockerImageBuildSettings
                {
                    File = "Dockerfile",
                    Tag = new string[] { destinationTag },
                    BuildArg = new string[] { $"{MyGetApiKeyKey}={context.EnvironmentVariable<string>(MyGetApiKeyKey, null)}" }
                }, Directory.GetCurrentDirectory());

                context.DockerPush(destinationTag);
                context.DockerRmi(destinationTag);
            }
        }
        else
        {
            context.Information($"Branch `{context.Options?.GitVersion?.BranchName}` is not listed in `ReleasableBranches`, no images will be built");
        }
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(CleanTask))]
[IsDependentOn(typeof(VersionTask))]
[IsDependentOn(typeof(BuildTask))]
[IsDependentOn(typeof(ScanPackagesTask))]
[IsDependentOn(typeof(TestAndCoverTask))]
[IsDependentOn(typeof(EcrDeployBuildTask))]
[IsDependentOn(typeof(DockerBuildAndPushTask))]
[IsDependentOn(typeof(OctoDeployTask))]
public class DefaultTask : FrostingTask
{
}

[TaskName(nameof(VerifyAndReleaseNext))]
[IsDependentOn(typeof(VersionTask))]
[IsDependentOn(typeof(ParameterStoreUploadTask))]
[IsDependentOn(typeof(ServerlessDeployTask))]
[IsDependentOn(typeof(ReleaseTask))]
public sealed class VerifyAndReleaseNext : FrostingTask
{
} 
