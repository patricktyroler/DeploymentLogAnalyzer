using CommandLine;
using CommandLine.Text;


class CommandLineOptions
{
    [Option('d', "delay", Required=false, HelpText = "How long to wait in minutes before getting the logs and parsing them.")]
    public float Delay { get; set; }

    [Option("deployment",Required=true, HelpText = "Deployment name.")]
    public string Deployment { get; set; }
    [Option("project", Required = true, HelpText = "Project name")]
    public string Project { get; set; }
}

