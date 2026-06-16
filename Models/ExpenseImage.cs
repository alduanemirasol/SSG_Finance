using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MyMvcApp.Models
{
    [Table("expense_images")]
    public class ExpenseImage
    {
        [Key]
        [Column("image_id")]
        public int ImageId { get; set; }

        [Required]
        [Column("expense_id")]
        public int ExpenseId { get; set; }

        [Required]
        [Column("image_path")]
        public string ImagePath { get; set; } = null!;

        [Column("uploaded_at")]
        public DateTime UploadedAt { get; set; } = DateTime.Now;

        [ForeignKey(nameof(ExpenseId))]
        public Expense Expense { get; set; } = null!;
    }
}
