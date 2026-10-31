# adapter-with-getdbconnection

Buildable repo with one legacy RS0030 violation (`Sample.Adapters.Repo.Load` calls `GetDbConnection`; EF facade stubbed in `EfStub.cs`). Used by US2-AS43/AS44.

Manual steps (from `the repository root/`):

1. Pack to the local feed read by `nuget.config` (`../../../../out`, i.e. `<repo>/out`):
   `dotnet pack src/KirillMaster.QualityGates.Build -c Release -o <repo>/out`
2. Copy this folder to a temp dir; set the `local` source in its `nuget.config` to the feed's absolute path.
3. `dotnet build -p:QualityGatesGrandfatherScan=true` -> exit 0; `src/Sample.Adapters/obj/quality/Sample.Adapters.net10.0.sarif` contains RS0030 for `Repo.Load`.
4. `dotnet run --project src/KirillMaster.QualityGates.Cli -- grandfather --repo <tmp>` -> writes `quality/grandfathered/Sample.Adapters.GlobalSuppressions.cs`.
5. `dotnet build` -> exit 0. Add `src/Sample.Adapters/Other.cs` with another `GetDbConnection` call -> build fails with exactly one RS0030, in `Other.cs`.
