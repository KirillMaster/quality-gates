namespace KirillMaster.QualityGates.Cli;

public static class Program
{
    public static int Main(string[] args) => CliHost.Run(args, Console.Out, Console.Error);
}
