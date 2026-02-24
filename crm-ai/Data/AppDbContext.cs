namespace crm_ai.Data
{
    using crm_ai.Models;
    using Microsoft.EntityFrameworkCore;

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options) { }

        public DbSet<Customer> Customers { get; set; }
        public DbSet<CustomerAddress> CustomerAddresses { get; set; }

        public DbSet<Transaction> Transactions { get; set; }
        public DbSet<Visit> Visits { get; set; }
        public DbSet<Booking> Bookings { get; set; }

        public DbSet<Site> Sites { get; set; }
        public DbSet<Brand> Brands { get; set; }

        public DbSet<PaymentMethod> PaymentMethods { get; set; }
        public DbSet<OrderStatus> OrderStatuses { get; set; }
        public DbSet<BookingStatus> BookingStatuses { get; set; }

        public DbSet<TreeNode> TreeNodes { get; set; }

        public DbSet<Selection> Selections { get; set; }
        public DbSet<SelectionGroup> SelectionGroups { get; set; }
        public DbSet<SelectionRule> SelectionRules { get; set; }
        public DbSet<SelectionExecution> SelectionExecutions { get; set; }
        public DbSet<SelectionExecutionUser> SelectionExecutionUsers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>()
                .HasKey(c => c.Id);

            modelBuilder.Entity<TreeNode>()
                .HasOne(t => t.Parent)
                .WithMany(t => t.Children)
                .HasForeignKey(t => t.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<SelectionGroup>()
                .HasOne(g => g.ParentGroup)
                .WithMany(g => g.ChildGroups)
                .HasForeignKey(g => g.ParentGroupId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

}
