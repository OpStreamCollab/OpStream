using Microsoft.EntityFrameworkCore;
using OpStream.Server.Storage.EntityFrameworkCore;

namespace OpStream.Server.Storage.SQLite;

public class SqliteOpStreamDbContext : OpStreamDbContext
{
    public SqliteOpStreamDbContext(DbContextOptions<SqliteOpStreamDbContext> options) : base(options)
    {
    }
}
