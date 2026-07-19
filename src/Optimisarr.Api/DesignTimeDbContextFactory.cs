using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Optimisarr.Data;

namespace Optimisarr.Api;

/// <summary>Creates the model without starting queue workers when EF generates migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OptimisarrDbContext>
{
    public OptimisarrDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite("Data Source=optimisarr-design.db")
            .Options;
        return new OptimisarrDbContext(options);
    }
}
