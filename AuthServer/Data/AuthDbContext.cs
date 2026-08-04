using Microsoft.EntityFrameworkCore;
using AuthServer.Models;

namespace AuthServer.Data;

public class AuthDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AuthLog> AuthLogs => Set<AuthLog>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();
    public DbSet<LaunchTicket> LaunchTickets => Set<LaunchTicket>();

    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.Uid).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.Role);
        modelBuilder.Entity<AppSettings>().HasData(new AppSettings { Id = 1 });
        base.OnModelCreating(modelBuilder);
    }
}