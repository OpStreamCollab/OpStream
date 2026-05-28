using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpStream.Server.Storage.SqlServer;

public class SqlServerOpStreamDbContextFactory : IDesignTimeDbContextFactory<SqlServerOpStreamDbContext>
{
    public SqlServerOpStreamDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SqlServerOpStreamDbContext>();
        optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=OpStream;Trusted_Connection=True;MultipleActiveResultSets=true");

        return new SqlServerOpStreamDbContext(optionsBuilder.Options);
    }
}
