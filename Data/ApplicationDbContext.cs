using Microsoft.EntityFrameworkCore;
using MyMvcApp.Models;

namespace MyMvcApp.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<Account> Accounts { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Course> Courses { get; set; }
        public DbSet<AcademicProfile> AcademicProfiles { get; set; }
        public DbSet<SchoolYear> SchoolYears { get; set; }
        public DbSet<FullAmount> FullAmounts { get; set; }
        public DbSet<OrgFeePayment> OrgFeePayments { get; set; }
        public DbSet<OtherFund> OtherFunds { get; set; }
        public DbSet<Expense> Expenses { get; set; }
        public DbSet<Receipt> Receipts { get; set; }
        public DbSet<TreasurerSignature> TreasurerSignatures { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Account configuration
            modelBuilder.Entity<Account>(entity =>
            {
                entity.HasKey(e => e.AccountId);
                entity.Property(e => e.AccountId).HasColumnName("account_id");
                entity.Property(e => e.SchoolId).IsRequired().HasMaxLength(50).HasColumnName("school_id");
                entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(150).HasColumnName("password_hash");
                entity.Property(e => e.Email).HasMaxLength(150).HasColumnName("email");
                entity.Property(e => e.Role).IsRequired().HasConversion<string>().HasColumnName("roles");
                entity.Property(e => e.RequestStatus).IsRequired().HasConversion<string>().HasDefaultValue(RequestStatus.Pending).HasColumnName("request_status");
                entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true).HasColumnName("is_active");
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP").HasColumnName("created_at");
                entity.Property(e => e.PasswordResetToken).HasMaxLength(255).HasColumnName("password_reset_token");
                entity.Property(e => e.PasswordResetTokenExpires).HasColumnName("password_reset_token_expires");

                entity.HasIndex(e => e.SchoolId).IsUnique().HasDatabaseName("uq_accounts_school_id");
            });

            // User configuration
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(e => e.UserId);
                entity.Property(e => e.UserId).HasColumnName("user_id");
                entity.Property(e => e.AccountId).IsRequired().HasColumnName("account_id");
                entity.Property(e => e.FirstName).HasMaxLength(100).HasColumnName("first_name");
                entity.Property(e => e.LastName).HasMaxLength(100).HasColumnName("last_name");
                entity.Property(e => e.MiddleName).HasMaxLength(100).HasColumnName("middle_name");

                entity.HasIndex(e => e.AccountId).IsUnique().HasDatabaseName("uq_users_account");

                entity.HasOne(e => e.Account)
                    .WithOne(a => a.User)
                    .HasForeignKey<User>(e => e.AccountId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // Course configuration
            modelBuilder.Entity<Course>(entity =>
            {
                entity.HasKey(e => e.CourseId);
                entity.Property(e => e.CourseId).HasColumnName("course_id");
                entity.Property(e => e.CourseCode).IsRequired().HasMaxLength(20).HasColumnName("course_code");
                entity.Property(e => e.CourseName).HasMaxLength(100).HasColumnName("course_name");

                entity.HasIndex(e => e.CourseCode).IsUnique().HasDatabaseName("uq_course_code");
            });

            // AcademicProfile configuration
            modelBuilder.Entity<AcademicProfile>(entity =>
            {
                entity.HasKey(e => e.AcademicProfileId);
                entity.Property(e => e.AcademicProfileId).HasColumnName("academic_profile_id");
                entity.Property(e => e.UserId).IsRequired().HasColumnName("user_id");
                entity.Property(e => e.CourseId).IsRequired().HasColumnName("course_id");
                entity.Property(e => e.SchoolYearId).HasColumnName("school_year_id");
                entity.Property(e => e.SemesterEntered).HasConversion<string>().HasColumnName("semester_entered");
                entity.Property(e => e.YearLevel).HasColumnName("year_level");
                entity.Property(e => e.Section).HasMaxLength(50).HasColumnName("section");
                entity.Property(e => e.AcademicStatus).IsRequired().HasConversion<string>().HasDefaultValue(AcademicStatus.Enrolled).HasColumnName("academic_status");

                entity.HasIndex(e => e.UserId).IsUnique().HasDatabaseName("uq_academic_user");

                entity.HasOne(e => e.User)
                    .WithOne(u => u.AcademicProfile)
                    .HasForeignKey<AcademicProfile>(e => e.UserId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.Course)
                    .WithMany(c => c.AcademicProfiles)
                    .HasForeignKey(e => e.CourseId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.SchoolYear)
                    .WithMany()
                    .HasForeignKey(e => e.SchoolYearId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // SchoolYear configuration
            modelBuilder.Entity<SchoolYear>(entity =>
            {
                entity.HasKey(e => e.SchoolYearId);
                entity.Property(e => e.SchoolYearId).HasColumnName("school_year_id");
                entity.Property(e => e.YearStart).IsRequired().HasColumnName("year_start");
                entity.Property(e => e.YearEnd).IsRequired().HasColumnName("year_end");
                entity.Property(e => e.YearStatus)
                    .IsRequired()
                    .HasConversion<string>()
                    .HasDefaultValue(YearStatus.Current)
                    .HasColumnName("year_status");

                entity.ToTable(t => t.HasCheckConstraint("chk_year_range", "year_end = year_start + 1"));
            });

            // FullAmount configuration
            modelBuilder.Entity<FullAmount>(entity =>
            {
                entity.HasKey(e => e.FullAmountId);
                entity.Property(e => e.FullAmountId).HasColumnName("full_amount_id");
                entity.Property(e => e.SchoolYearId).IsRequired().HasColumnName("school_year_id");
                entity.Property(e => e.Semester)
                    .IsRequired()
                    .HasColumnName("semester")
                    .HasConversion<string>();
                entity.Property(e => e.Amount).IsRequired().HasPrecision(10, 2).HasColumnName("amount");
                entity.Property(e => e.SemesterStatus)
                    .IsRequired()
                    .HasConversion<string>()
                    .HasDefaultValue(SemesterStatus.Current)
                    .HasColumnName("semester_status");

                entity.HasOne(e => e.SchoolYear)
                    .WithMany(sy => sy.FullAmounts)
                    .HasForeignKey(e => e.SchoolYearId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // OrgFeePayment configuration
            modelBuilder.Entity<OrgFeePayment>(entity =>
            {
                entity.ToTable("org_fee_payments");
                entity.HasKey(e => e.PaymentId);
                entity.Property(e => e.PaymentId).HasColumnName("payment_id");
                entity.Property(e => e.UserId).IsRequired().HasColumnName("user_id");
                entity.Property(e => e.FullAmountId).IsRequired().HasColumnName("full_amount_id");
                entity.Property(e => e.Amount).IsRequired().HasPrecision(10, 2).HasColumnName("amount");
                entity.Property(e => e.PaymentStatus)
                    .IsRequired()
                    .HasColumnName("payment_status")
                    .HasColumnType("varchar(10)")
                    .HasConversion(
                        v => v.ToString(),
                        v => (PaymentStatus)Enum.Parse(typeof(PaymentStatus), v)
                    );
                entity.Property(e => e.ReceivedBy).IsRequired().HasColumnName("received_by");
                entity.Property(e => e.PaymentDate).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP").HasColumnName("payment_date");

                entity.HasOne(e => e.User)
                    .WithMany(u => u.Payments)
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.FullAmount)
                    .WithMany(fa => fa.OrgFeePayments)
                    .HasForeignKey(e => e.FullAmountId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.Receiver)
                    .WithMany(a => a.ReceivedPayments)
                    .HasForeignKey(e => e.ReceivedBy)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // OtherFund configuration
            modelBuilder.Entity<OtherFund>(entity =>
            {
                entity.HasKey(e => e.FundId);
                entity.Property(e => e.FundId).HasColumnName("fund_id");
                entity.Property(e => e.Source).HasMaxLength(200).HasColumnName("source");
                entity.Property(e => e.Description).HasColumnType("text").HasColumnName("description");
                entity.Property(e => e.Amount).IsRequired().HasPrecision(10, 2).HasColumnName("amount");
                entity.Property(e => e.ReceivedBy).IsRequired().HasColumnName("received_by");
                entity.Property(e => e.ReceivedDate).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP").HasColumnName("received_date");
                entity.Property(e => e.SchoolYearId).HasColumnName("school_year_id");

                entity.HasOne(e => e.Receiver)
                    .WithMany(a => a.ReceivedFunds)
                    .HasForeignKey(e => e.ReceivedBy)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.SchoolYear)
                    .WithMany()
                    .HasForeignKey(e => e.SchoolYearId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            // Expense configuration
            modelBuilder.Entity<Expense>(entity =>
            {
                entity.HasKey(e => e.ExpenseId);
                entity.Property(e => e.ExpenseId).HasColumnName("expense_id");
                entity.Property(e => e.Description).HasColumnType("text").HasColumnName("description");
                entity.Property(e => e.Amount).IsRequired().HasPrecision(10, 2).HasColumnName("amount");
                entity.Property(e => e.RecordedBy).IsRequired().HasColumnName("recorded_by");
                entity.Property(e => e.ExpenseDate).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP").HasColumnName("expense_date");
                entity.Property(e => e.SchoolYearId).HasColumnName("school_year_id");

                entity.HasOne(e => e.Recorder)
                    .WithMany(a => a.RecordedExpenses)
                    .HasForeignKey(e => e.RecordedBy)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.SchoolYear)
                    .WithMany()
                    .HasForeignKey(e => e.SchoolYearId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            // Receipt configuration
            modelBuilder.Entity<Receipt>(entity =>
            {
                entity.HasKey(e => e.ReceiptId);
                entity.Property(e => e.ReceiptId).HasColumnName("receipt_id");
                entity.Property(e => e.ReceiptNumber).IsRequired().HasMaxLength(50).HasColumnName("receipt_number");
                entity.Property(e => e.PaymentId).HasColumnName("payment_id");
                entity.Property(e => e.IssuedBy).IsRequired().HasColumnName("issued_by");

                entity.HasIndex(e => e.ReceiptNumber).IsUnique().HasDatabaseName("uq_receipt_number");

                entity.HasOne(e => e.Payment)
                    .WithMany(p => p.Receipts)
                    .HasForeignKey(e => e.PaymentId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasOne(e => e.Issuer)
                    .WithMany(a => a.IssuedReceipts)
                    .HasForeignKey(e => e.IssuedBy)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // TreasurerSignature configuration
            modelBuilder.Entity<TreasurerSignature>(entity =>
            {
                entity.ToTable("treasurer_signatures");
                entity.HasKey(e => e.SignatureId);
                entity.Property(e => e.SignatureId).HasColumnName("signature_id");
                entity.Property(e => e.AccountId).IsRequired().HasColumnName("account_id");
                entity.Property(e => e.SignatureData).IsRequired().HasColumnType("mediumtext").HasColumnName("signature_data");
                entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP").HasColumnName("created_at");
                entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true).HasColumnName("is_active");

                entity.HasOne(e => e.Account)
                    .WithMany()
                    .HasForeignKey(e => e.AccountId)
                    .OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}
