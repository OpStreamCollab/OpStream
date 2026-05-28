using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpStream.Server.Storage.MySQL;

public class MySqlOpStreamDbContextFactory : IDesignTimeDbContextFactory<MySqlOpStreamDbContext>
{
    public MySqlOpStreamDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MySqlOpStreamDbContext>();
        optionsBuilder.UseMySql("Server=localhost;Database=opstream;Uid=root;Pwd=root", new MySqlServerVersion(new Version(8, 0, 36)));

        return new MySqlOpStreamDbContext(optionsBuilder.Options);
    }
}
