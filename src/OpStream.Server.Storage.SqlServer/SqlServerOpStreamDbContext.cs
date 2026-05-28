using Microsoft.EntityFrameworkCore;
using OpStream.Server.Storage.EntityFrameworkCore;

namespace OpStream.Server.Storage.SqlServer;

public class SqlServerOpStreamDbContext : OpStreamDbContext
{
    public SqlServerOpStreamDbContext(DbContextOptions<SqlServerOpStreamDbContext> options) : base(options)
    {
    }
}
