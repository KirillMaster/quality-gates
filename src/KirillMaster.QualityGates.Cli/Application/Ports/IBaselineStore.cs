using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application.Ports;

public interface IBaselineStore
{
    /// <summary>Returns null when the baseline file does not exist (bootstrap mode).</summary>
    Baseline? Load(string path);

    void Save(string path, Baseline baseline);
}
