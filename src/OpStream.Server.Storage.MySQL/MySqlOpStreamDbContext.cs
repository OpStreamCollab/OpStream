using Microsoft.EntityFrameworkCore;
using OpStream.Server.Storage.EntityFrameworkCore;

namespace OpStream.Server.Storage.MySQL;

public class MySqlOpStreamDbContext : OpStreamDbContext
{
    public MySqlOpStreamDbContext(DbContextOptions<MySqlOpStreamDbContext> options) : base(options)
    {
    }
}
