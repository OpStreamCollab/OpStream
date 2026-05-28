using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpStream.Server.Storage.SQLite;

public class SqliteOpStreamDbContextFactory : IDesignTimeDbContextFactory<SqliteOpStreamDbContext>
{
    public SqliteOpStreamDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SqliteOpStreamDbContext>();
        optionsBuilder.UseSqlite("Data Source=opstream.db");

        return new SqliteOpStreamDbContext(optionsBuilder.Options);
    }
}
