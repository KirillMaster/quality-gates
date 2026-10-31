namespace KirillMaster.QualityGates.Cli.Domain.Model;

public enum Layer { Core, Adapter, Test, Excluded }

public enum Verdict { Pass, Fail, Warn, Skip, Timeout, NotApplicable }

public enum Policy { Blocking, ReportOnly, Skip }

public enum RunMode { Pr, MutationDiff, Nightly, Audit }

public enum GateId { StaticRatchet, DiffCoverage, CoverageRatchet, MutationDiff, MutationRatchet, NewCodeViolations, Format, Duplication, BaselineGrowth, ChangeCoupling, PrSize, ArchDelta, CognitiveComplexityDelta, ToggleExpiry, DeadPublic, TransitiveFanOut, ProcessReports, FeatureReadme, ArchFlows, ChangedCodeChangedTests, ArchDeltaIl, MainSequence, BranchAge, BuildTrend, MovedLineCoverage }

public enum ResultLayer { Core, Adapter, All }
