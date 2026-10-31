namespace Microsoft.EntityFrameworkCore.Infrastructure { public sealed class DatabaseFacade { } }

namespace Microsoft.EntityFrameworkCore
{
    public static class RelationalDatabaseFacadeExtensions
    {
        public static System.Data.Common.DbConnection GetDbConnection(this Infrastructure.DatabaseFacade f) => throw new System.NotSupportedException();

        public static System.Threading.Tasks.Task OpenConnectionAsync(this Infrastructure.DatabaseFacade f, System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
    }
}
