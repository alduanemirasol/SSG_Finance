using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MyMvcApp.Models
{
    [Table("users")]
    public class User
    {
        [Key]
        public int UserId { get; set; }

        [Required]
        public int AccountId { get; set; }

        [StringLength(100)]
        [Column("first_name")]
        public string? FirstName { get; set; }

        [StringLength(100)]
        [Column("last_name")]
        public string? LastName { get; set; }

        [StringLength(100)]
        [Column("middle_name")]
        public string? MiddleName { get; set; }

        [StringLength(500)]
        [Column("avatar_path")]
        public string? AvatarPath { get; set; }

        // Navigation properties
        [ForeignKey("AccountId")]
        public Account Account { get; set; } = null!;
        
        public AcademicProfile? AcademicProfile { get; set; }
        public ICollection<OrgFeePayment> Payments { get; set; } = new List<OrgFeePayment>();
    }
}
