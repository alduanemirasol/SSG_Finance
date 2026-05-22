using System.ComponentModel.DataAnnotations;

namespace MyMvcApp.Models
{
    public class StudentViewModel
    {
        public int StudentId { get; set; }
        
        [Required]
        [StringLength(100)]
        public string FullName { get; set; } = string.Empty;
        
        [Required]
        [StringLength(50)]
        public string CourseCode { get; set; } = string.Empty;
        
        [Required]
        [StringLength(20)]
        public string YearSection { get; set; } = string.Empty;
        
        public int AccountId { get; set; }
        
        public string Role { get; set; } = string.Empty;
        
        public bool IsActive { get; set; }
        
        [Required]
        [StringLength(50)]
        public string SchoolId { get; set; } = string.Empty;
        
        [StringLength(50)]
        public string AcademicStatus { get; set; } = string.Empty;

        public int? SchoolYearId { get; set; }

        public Semester? SemesterEntered { get; set; }
    }
}
