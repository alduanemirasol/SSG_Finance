using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MyMvcApp.Models
{
    [Table("academic_profile")]
    public class AcademicProfile
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int AcademicProfileId { get; set; }

        [Required]
        public int UserId { get; set; }

        [Required]
        public int CourseId { get; set; }

        public int? SchoolYearId { get; set; }

        [Column("semester_entered")]
        public Semester? SemesterEntered { get; set; }

        [Column("year_level")]
        public int? YearLevel { get; set; }

        [StringLength(50)]
        public string? Section { get; set; }

        [Required]
        [Column("academic_status")]
        public AcademicStatus AcademicStatus { get; set; } = AcademicStatus.Enrolled;

        // Navigation properties
        [ForeignKey("UserId")]
        public User User { get; set; } = null!;

        [ForeignKey("CourseId")]
        public Course Course { get; set; } = null!;

        [ForeignKey("SchoolYearId")]
        public SchoolYear? SchoolYear { get; set; }
    }

    public enum AcademicStatus
    {
        Enrolled,
        Transferred,
        Graduated,
        Dropped
    }
}
