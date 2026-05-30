using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MyMvcApp.Models;

public enum PaymentStatus
{
    Paid,
    Partial
}

[Table("org_fee_payments")]
public class OrgFeePayment
{
    [Key]
    [Column("payment_id")]
    public int PaymentId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("full_amount_id")]
    public int FullAmountId { get; set; }

    [Column("amount")]
    public decimal Amount { get; set; }

    [Column("payment_status")]
    public PaymentStatus PaymentStatus { get; set; }

    [Column("received_by")]
    public int ReceivedBy { get; set; }

    [Column("payment_date")]
    public DateTime PaymentDate { get; set; }

    // Snapshot of the student's year level and section at the moment of payment,
    // so a receipt always reflects the year level the student had when they paid,
    // even after they advance in later school years. Nullable for existing rows.
    [Column("year_level_at_payment")]
    public int? YearLevelAtPayment { get; set; }

    [Column("section_at_payment")]
    public string? SectionAtPayment { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public FullAmount FullAmount { get; set; } = null!;
    public Account Receiver { get; set; } = null!;
    public ICollection<Receipt> Receipts { get; set; } = new List<Receipt>();
}