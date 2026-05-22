using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MyMvcApp.Models
{
    [Table("full_amount")]
    public class FullAmount
    {
        [Key]
        [Column("full_amount_id")]

        public int FullAmountId { get; set; }
        [Required]
        [Column("school_year_id")]
        public int SchoolYearId { get; set; }

        [Required]
        [Column("semester")]
        public Semester Semester { get; set; }

        [Required]
        [Column("amount")]
        public decimal Amount { get; set; }

        [Required]
        [Column("semester_status")]
        public SemesterStatus SemesterStatus { get; set; } = SemesterStatus.Current;

        [ForeignKey("SchoolYearId")]
        public SchoolYear SchoolYear { get; set; } = null!;

        public virtual ICollection<OrgFeePayment> OrgFeePayments { get; set; } = new List<OrgFeePayment>();
    }

    public enum Semester
    {
        First,
        Second
    }

    public enum SemesterStatus
    {
        Current,
        Ended
    }
}
