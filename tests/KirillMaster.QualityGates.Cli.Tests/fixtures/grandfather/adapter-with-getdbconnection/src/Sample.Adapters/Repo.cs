using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Sample.Adapters;

public static class Repo
{
    public static object Load(DatabaseFacade db) => Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(db);
}
