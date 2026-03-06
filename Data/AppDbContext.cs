using Microsoft.EntityFrameworkCore;
using VideoDownloader.Models;

namespace VideoDownloader.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) 
        : base(options) { }

    public DbSet<Device> Devices { get; set; }
}