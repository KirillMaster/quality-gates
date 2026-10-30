namespace KirillMaster.QualityGates.Cli.Domain.Model;

public sealed record DiffCoverageThresholds(double Core = 90, double Adapter = 80);

public sealed record MutationSettings(double CoreThreshold = 80, int DiffBudgetMinutes = 30, int NightlyBudgetMinutes = 240);

public sealed record NotificationSettings(string? SlackChannel = null, string? JiraTechDebtIssue = null, string? S3Bucket = null, string? S3Prefix = null);

public sealed record GatesConfig(
    string Service,
    DiffCoverageThresholds DiffCoverage,
    MutationSettings Mutation,
    IReadOnlyDictionary<string, Layer> LayerOverrides,
    bool IntegrationTestsEnabled = true,
    NotificationSettings? Notifications = null)
{
    public static GatesConfig Default { get; } = new(
        string.Empty,
        new DiffCoverageThresholds(),
        new MutationSettings(),
        new Dictionary<string, Layer>(),
        true);
}
