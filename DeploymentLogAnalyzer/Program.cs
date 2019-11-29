using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using CommandLine;
using Improbable.SpatialOS.Deployment.V1Beta1;
using Improbable.SpatialOS.ServiceAccount.V1Alpha1;
using Improbable.SpatialOS.Platform.Common;

class Program
{
    private static DeploymentServiceClient DeploymentServiceClient;

    private static readonly ServiceAccountServiceClient ServiceAccountServiceClient = ServiceAccountServiceClient.Create();


    private static readonly string WorkerLogPath =
        "https://proxy.improbable.io/@proxyhost/fsdash.worker_nanny.workers.{0}-{1}.sim.{2}.internal.improbable.io/data/improbable/logs/UnrealWorker/UnrealWorker0.log";

    private static readonly string SimPathA = "gce-us-central1-a";
    private static readonly string SimPathB = "gce-us-central1-b";
    private static readonly string SimPathC = "gce-us-central1-c";
    private static readonly string SimPathD = "gce-us-central1-d";
    static void Main(string[] args)
    {
        Parser.Default.ParseArguments<CommandLineOptions>(args).WithParsed<CommandLineOptions>(Run);
    }

    static void Run(CommandLineOptions options)
    {
        Console.WriteLine("Starting DeploymentServiceClient");
        DeploymentServiceClient = DeploymentServiceClient.Create();
        //Check if the deployment exists before waiting to get logs.
        var request = new GetRunningDeploymentByNameRequest()
        {
            DeploymentName = options.Deployment,
            ProjectName = options.Project,
            View = ViewType.Full
        };

        Console.WriteLine("Requesting deployment by name {0}", options.Deployment);
        var deploy = DeploymentServiceClient.GetRunningDeploymentByName(request);
        if (deploy == null)
        {
            Console.WriteLine("Requested deployment {0} does not exist in project {1}. Exiting.", options.Deployment, options.Project);
            return;
        }

        Console.WriteLine("Found deployment");
        {
            var testingFlag = new WorkerFlag()
            {
                Key = "ExtendedTesting",
                Value = "True",
                WorkerType = "UnrealWorker"
            };

            var flags = deploy.Deployment.WorkerFlags.Clone();

            flags.Add(testingFlag);

            DeploymentServiceClient.SetDeploymentWorkerFlags(new SetDeploymentWorkerFlagsRequest()
            {
                DeploymentId = deploy.Deployment.Id,
                WorkerFlags = {flags}
            });
        }
        Console.WriteLine("Worker flag set, sleeping for {0} minutes", options.Delay);
        //Sleep for {delay} minutes.
        Thread.Sleep((int)(1000f * 60f * options.Delay));

        //Set testing flag to false
        {
            var testingFlag = new WorkerFlag()
            {
                Key = "ExtendedTesting",
                Value = "False",
                WorkerType = "UnrealWorker"
            };

            var flags = deploy.Deployment.WorkerFlags.Clone();
            flags.Remove(flags.FirstOrDefault(a=> a.Key == "ExtendedTesting"));
            flags.Add(testingFlag);

            DeploymentServiceClient.SetDeploymentWorkerFlags(new SetDeploymentWorkerFlagsRequest()
            {
                DeploymentId = deploy.Deployment.Id,
                WorkerFlags = {flags}
            });
        }
        //Wait one minute to allow server processes to print outcomes to the log after testing is stopped.
        Thread.Sleep(60 * 1000);

        //Get log
        var authValue = PlatformRefreshTokenCredential.AutoDetected.Token.AccessToken;

        using var wc = new WebClient();
        wc.Headers.Add(HttpRequestHeader.Authorization, "Bearer " + authValue);
        List<string> log = null;
        #region AttemptDownloads
        try
        {
            log = wc.DownloadString(string.Format(WorkerLogPath, options.Project, options.Deployment, SimPathA)).Split('\n').ToList();
        }
        catch (WebException e)
        {
            if (e.Response.ToString().Contains("404"))
                Console.WriteLine("Log file was not found on " + SimPathA);
            else if (e.Response.ToString().Contains("401"))
            {
                Console.WriteLine(
                    "Unauthorized to access resource, check your refresh token and permission to access deployments in " +
                    options.Project);
                return;
            }
        }

        if (log == null)
        {
            try
            {
                log = wc.DownloadString(string.Format(WorkerLogPath, options.Project, options.Deployment, SimPathB)).Split('\n').ToList();
            }
            catch (WebException e)
            {
                if (e.Response.ToString().Contains("404"))
                    Console.WriteLine("Log file was not found on " + SimPathB);
                else if (e.Response.ToString().Contains("401"))
                {
                    Console.WriteLine(e.ToString());
                    return;
                }
            }
        }

        if (log == null)
        {
            try
            {
                log = wc.DownloadString(string.Format(WorkerLogPath, options.Project, options.Deployment, SimPathC)).Split('\n').ToList();
            }
            catch (WebException e)
            {
                if (e.Response.ToString().Contains("404"))
                    Console.WriteLine("Log file was not found on " + SimPathC);
                else if (e.Response.ToString().Contains("401"))
                {
                    Console.WriteLine(e.ToString());
                    return;
                }
            }
        }

        if (log == null)
        {
            try
            {
                log = wc.DownloadString(string.Format(WorkerLogPath, options.Project, options.Deployment, SimPathD)).Split('\n').ToList();
            }
            catch (WebException e)
            {
                if (e.Response.ToString().Contains("404"))
                    Console.WriteLine("Log file was not found on " + SimPathD);
                else if (e.Response.ToString().Contains("401"))
                {
                    Console.WriteLine(e.ToString());
                    return;
                }
            }
        }

        if (log == null)
        {
            Console.WriteLine("Raw worker log could not be found - Exiting.");
            return;
        }
        #endregion

        Dictionary<string, int> logEntries = new Dictionary<string, int>();
        //Build a dictionary so we can count the errors/warnings that are identical and present a nicer output, print them anyway in case we'd like to see them in order.
        foreach (var line in log)
        {
            if (line.ToLower().Contains("warning"))
            {
                Console.WriteLine("WARNING: " + line);
                var trimmedLine = line.Substring(line.IndexOf(']') + 1);
                if (logEntries.ContainsKey("WARNING: " + trimmedLine))
                    logEntries["WARNING: " + trimmedLine]++;
                else
                    logEntries.Add("WARNING: " + trimmedLine, 1);
            }

            if (line.ToLower().Contains("error"))
            {
                Console.WriteLine("ERROR: " + line);
                var trimmedLine = line.Substring(line.IndexOf(']') + 1);
                if (logEntries.ContainsKey("ERROR: " + trimmedLine))
                    logEntries["ERROR: " + trimmedLine]++;
                else
                    logEntries.Add("ERROR: " + trimmedLine, 1);

            }
        }

        Console.WriteLine("+++ Clean Error/Warning Output");
        StartPrint:
        if (logEntries.Count > 0)
        {
            var entry = logEntries.ElementAt(0);
            logEntries.Remove(entry.Key);
            Console.WriteLine("( {0} ) {1}", entry.Value, entry.Key);
            goto StartPrint;
        }
    }
}
