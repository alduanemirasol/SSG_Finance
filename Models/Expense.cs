using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MyMvcApp.Models
{
    [Table("expenses")]
    public class Expense
    {
        [Key]
        public int ExpenseId { get; set; }

        [Column(TypeName = "text")]
        public string? Description { get; set; }

        [Required]
        [Column(TypeName = "decimal(10,2)")]
        public decimal Amount { get; set; }

        [Required]
        public int RecordedBy { get; set; }

        [Required]
        [Column("expense_date")]
        public DateTime ExpenseDate { get; set; } = DateTime.Now;

        [Column("school_year_id")]
        public int? SchoolYearId { get; set; }

        // Navigation properties
        [ForeignKey("RecordedBy")]
        public Account Recorder { get; set; } = null!;

        [ForeignKey(nameof(SchoolYearId))]
        public SchoolYear? SchoolYear { get; set; }

        // Receipt images (hardcopy photos) attached to this expense
        public ICollection<ExpenseImage> Images { get; set; } = new List<ExpenseImage>();
    }
}
