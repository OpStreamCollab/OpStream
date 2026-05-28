using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpStream.Server.Storage.PostgreSQL;

public class PostgreSqlOpStreamDbContextFactory : IDesignTimeDbContextFactory<PostgreSqlOpStreamDbContext>
{
    public PostgreSqlOpStreamDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PostgreSqlOpStreamDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=opstream;Username=postgres;Password=postgres");

        return new PostgreSqlOpStreamDbContext(optionsBuilder.Options);
    }
}
