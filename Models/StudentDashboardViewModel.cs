namespace MyMvcApp.Models
{
    public class StudentDashboardViewModel
    {
        public List<StudentPaymentHistoryViewModel> PaymentHistory { get; set; } = new();
        public List<StudentReceiptViewModel> Receipts { get; set; } = new();
    }

    public class StudentReceiptViewModel
    {
        public int ReceiptId { get; set; }
        public string ReceiptNumber { get; set; } = string.Empty;
        public int PaymentId { get; set; }
        public DateTime? IssueDate { get; set; }
        public int IssuedByAccountId { get; set; }
        public string IssuedByName { get; set; } = string.Empty;
        public string StudentName { get; set; } = string.Empty;
        public string StudentId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Term { get; set; } = string.Empty;
        public string Semester { get; set; } = string.Empty;
        public string Course { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        // The student's year level and section at the time this payment was made,
        // so the receipt reflects their standing then, not their current standing.
        public int? YearLevelAtPayment { get; set; }
        public string? SectionAtPayment { get; set; }
    }

    public class StudentPaymentHistoryViewModel
    {
        public string Term { get; set; } = string.Empty;
        public string Semester { get; set; } = string.Empty;
        public string Course { get; set; } = string.Empty;
        public string OverallStatus { get; set; } = string.Empty;
        public List<StudentPaymentViewModel> Payments { get; set; } = new();
    }

    public class StudentPaymentViewModel
    {
        public int PaymentId { get; set; }
        public string Semester { get; set; } = string.Empty;
        public string Course { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? PaymentDate { get; set; }
        public string TreasurerName { get; set; } = string.Empty;
        public string ReceiptNumber { get; set; } = string.Empty;
        public DateTime? ReceiptIssueDate { get; set; }
        public string ReceiptIssuedBy { get; set; } = string.Empty;
        public int ReceiptIssuedByAccountId { get; set; }
    }
}