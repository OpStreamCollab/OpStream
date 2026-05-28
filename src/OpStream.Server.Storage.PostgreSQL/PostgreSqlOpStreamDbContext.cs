using Microsoft.EntityFrameworkCore;
using OpStream.Server.Storage.EntityFrameworkCore;

namespace OpStream.Server.Storage.PostgreSQL;

public class PostgreSqlOpStreamDbContext : OpStreamDbContext
{
    public PostgreSqlOpStreamDbContext(DbContextOptions<PostgreSqlOpStreamDbContext> options) : base(options)
    {
    }
}
