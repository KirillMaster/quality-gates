using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application;

public static class ExitCodeMapper
{
    public const int Success = 0;
    public const int Failed = 1;
    public const int Warned = 2;
    public const int Error = 3;

    public static int Map(RunReport report)
    {
        var worst = Success;
        foreach (var result in report.Results)
        {
            var code = result.Verdict switch
            {
                Verdict.Fail => Failed,
                Verdict.Warn or Verdict.Timeout => Warned,
                _ => Success
            };
            if (code == Failed)
            {
                return Failed;
            }

            worst = Math.Max(worst, code);
        }

        return worst;
    }
}
