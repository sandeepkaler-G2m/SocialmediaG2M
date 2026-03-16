using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Models;

namespace SocialMediaPanel.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("usersinfo");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Email).IsUnique();

                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(100);
                entity.Property(e => e.Email).HasColumnName("email").IsRequired().HasMaxLength(150);
                entity.Property(e => e.Password).HasColumnName("password").IsRequired().HasMaxLength(255);
                entity.Property(e => e.CompanyName).HasColumnName("company_name").HasMaxLength(200);
                entity.Property(e => e.CompanySize).HasColumnName("company_size").HasMaxLength(50);
                entity.Property(e => e.CompanyType).HasColumnName("company_type").HasMaxLength(100);
                entity.Property(e => e.CompanyAddress).HasColumnName("company_address");
            });
        }
    }
}