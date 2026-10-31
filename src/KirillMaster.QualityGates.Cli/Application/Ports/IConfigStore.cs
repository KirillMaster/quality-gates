using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application.Ports;

public sealed record ConfigError(string FieldPath, string Message)
{
    public override string ToString() => $"{FieldPath}: {Message}";
}

public sealed record ConfigLoadResult(GatesConfig? Config, ConfigError? Error)
{
    public static ConfigLoadResult Ok(GatesConfig config) => new(config, null);

    public static ConfigLoadResult Fail(string fieldPath, string message) => new(null, new ConfigError(fieldPath, message));
}

public interface IConfigStore
{
    ConfigLoadResult Load(string path);
}
