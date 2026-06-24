using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BCrypt.Net;
using MyMvcApp.Models;
using MyMvcApp.Services;
using MyMvcApp.Data;

namespace MyMvcApp.Controllers;

public partial class HomeController : AppController
{
    [HttpPost]
    public async Task<IActionResult> AddOrgFeePayment([FromBody] AddOrgFeePaymentRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            // Parse semester from request
            var semesterInput = (request.Semester ?? string.Empty).Trim();
            Semester semester;
            if (semesterInput.Equals("First", StringComparison.OrdinalIgnoreCase) ||
                semesterInput.Equals("1st",   StringComparison.OrdinalIgnoreCase))
                semester = Semester.First;
            else if (semesterInput.Equals("Second", StringComparison.OrdinalIgnoreCase) ||
                     semesterInput.Equals("2nd",    StringComparison.OrdinalIgnoreCase))
                semester = Semester.Second;
            else
                return Json(new { success = false, message = "Invalid semester selected." });

            FullAmount? targetFee = null;
            SchoolYear? currentSchoolYear = null;

            if (request.FullAmountId > 0)
            {
                targetFee = await _context.FullAmounts
                    .Include(f => f.SchoolYear)
                    .FirstOrDefaultAsync(f => f.FullAmountId == request.FullAmountId);
            }
            else
            {
                currentSchoolYear = await _context.SchoolYears
                    .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

                if (currentSchoolYear == null)
                    return Json(new { success = false, message = "No active school year found. Please contact the administrator." });

                targetFee = await _context.FullAmounts
                    .Include(f => f.SchoolYear)
                    .FirstOrDefaultAsync(f => f.SchoolYearId == currentSchoolYear.SchoolYearId
                                           && f.Semester     == semester);
            }

            if (targetFee == null && request.FullAmountId > 0)
                return Json(new { success = false, message = "Fee not found." });

            if (targetFee == null)
                return Json(new { success = false, message = $"No fee set for {semesterInput} Semester of {currentSchoolYear!.YearStart}–{currentSchoolYear.YearEnd}. Please set the fee in Settings first." });

            var student = await _context.Users
                .Include(u => u.AcademicProfile)
                    .ThenInclude(ap => ap!.SchoolYear)
                .Include(u => u.Account)
                .FirstOrDefaultAsync(u => u.UserId == request.UserId
                                       && u.Account != null
                                       && (u.Account.Role == UserRole.Student || u.Account.Role == UserRole.Treasurer)
                                       && u.Account.RequestStatus == RequestStatus.Approved);

            if (student == null)
                return Json(new { success = false, message = "Student not found." });

            var studentExemptions = await GetExemptionsForUserAsync(request.UserId);

            if (!FeeRules.IsFeeApplicableToStudent(student.AcademicProfile, targetFee, studentExemptions))
                return Json(new { success = false, message = "This student is not charged for this semester because they entered after it." });

            // RULE: Can't pay for the selected school year until ALL applicable fees from
            // PREVIOUS school years are fully paid. Shared helper keeps this in lockstep
            // with the read path (GetStudentFeeStatus) that drives the treasurer UI.
            var unpaidPriorFee = await GetEarliestUnpaidPriorYearFeeAsync(
                request.UserId, student.AcademicProfile, targetFee, studentExemptions);

            if (unpaidPriorFee != null)
            {
                var semLabel = unpaidPriorFee.Semester == Semester.First ? "1st" : "2nd";
                var yrLabel  = unpaidPriorFee.SchoolYear != null
                    ? $"{unpaidPriorFee.SchoolYear.YearStart}–{unpaidPriorFee.SchoolYear.YearEnd}"
                    : "a previous school year";
                return Json(new
                {
                    success = false,
                    message = $"Cannot pay for this school year. The student still has an unpaid balance for the {semLabel} Semester of {yrLabel}. Previous school year balances must be settled first."
                });
            }

            // RULE: Can't pay 2nd semester until 1st semester (of the same school year) is fully paid —
            // but only if the 1st semester fee actually applies to this student.
            if (targetFee.Semester == Semester.Second)
            {
                var firstSemFee = await _context.FullAmounts
                    .FirstOrDefaultAsync(f => f.SchoolYearId == targetFee.SchoolYearId
                                           && f.Semester == Semester.First);

                if (firstSemFee != null
                    && FeeRules.IsFeeApplicableToStudent(student.AcademicProfile, firstSemFee, studentExemptions))
                {
                    var firstSemPaid = await _context.OrgFeePayments
                        .Where(p => p.UserId == request.UserId
                                 && p.FullAmountId == firstSemFee.FullAmountId)
                        .SumAsync(p => p.Amount);

                    if (firstSemPaid < firstSemFee.Amount)
                        return Json(new { success = false, message = "Cannot pay 2nd semester until the 1st semester is fully paid." });
                }
            }

            var accountIdStr = HttpContext.Session.GetString("AccountId");
            if (!int.TryParse(accountIdStr, out var receivedBy))
                return Json(new { success = false, message = "Invalid session." });

            // Check if student already has a Paid record for this fee
            var existingPayment = await _context.OrgFeePayments
                .FirstOrDefaultAsync(p => p.UserId       == request.UserId
                                       && p.FullAmountId == targetFee.FullAmountId
                                       && p.PaymentStatus == PaymentStatus.Paid);

            if (existingPayment != null)
                return Json(new { success = false, message = $"This student has already fully paid for the {semesterInput} Semester." });

            // ── KEY FIX: sum all previous partial payments first ──
            var previouslyPaid = await _context.OrgFeePayments
                .Where(p => p.UserId == request.UserId && p.FullAmountId == targetFee.FullAmountId)
                .SumAsync(p => p.Amount);

            var cumulativeTotal = previouslyPaid + request.Amount;

            // Reject an already-used receipt number up front, so we never create a payment
            // that then fails to attach its receipt (unique index uq_receipt_number).
            if (!string.IsNullOrWhiteSpace(request.ReceiptNumber)
                && await _context.Receipts.AnyAsync(r => r.ReceiptNumber == request.ReceiptNumber))
            {
                return Json(new { success = false, message = $"Receipt number \"{request.ReceiptNumber}\" has already been used. Please enter a different one." });
            }

            var payment = new OrgFeePayment
            {
                UserId        = request.UserId,
                FullAmountId  = targetFee.FullAmountId,
                Amount        = request.Amount,
                PaymentStatus = cumulativeTotal >= targetFee.Amount
                                    ? PaymentStatus.Paid
                                    : PaymentStatus.Partial,
                ReceivedBy    = receivedBy,
                PaymentDate   = DateTime.Now,
                // Freeze the student's standing at the time of payment so the receipt
                // never changes when the student advances in later school years.
                YearLevelAtPayment = student.AcademicProfile?.YearLevel,
                SectionAtPayment   = student.AcademicProfile?.Section
            };

            _context.OrgFeePayments.Add(payment);

            // Add the receipt to the SAME change-set so the payment and its receipt are
            // written in one transaction: if the receipt can't be saved, the payment is
            // rolled back too — no more orphan payment with no receipt number.
            if (!string.IsNullOrWhiteSpace(request.ReceiptNumber))
            {
                _context.Receipts.Add(new Receipt
                {
                    ReceiptNumber = request.ReceiptNumber,
                    Payment       = payment,   // EF fills in PaymentId after the payment insert
                    IssuedBy      = receivedBy
                });
            }

            await _context.SaveChangesAsync();

            await _sse.BroadcastAsync("payments-changed");
            return Json(new
            {
                success   = true,
                message   = "Payment recorded successfully.",
                paymentId = payment.PaymentId
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> AddOtherFund([FromBody] AddOtherFundRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var accountIdStr = HttpContext.Session.GetString("AccountId");
            if (!int.TryParse(accountIdStr, out var receivedBy))
                return Json(new { success = false, message = "Invalid session." });

            var receivedDate = request.ReceivedDate?.ToLocalTime() ?? DateTime.Now;

            // Find the school year that matches the fund date
            // School years run August-June, so Jan-July belongs to previous year_start
            var targetYearStart = receivedDate.Month >= 8 ? receivedDate.Year : receivedDate.Year - 1;
            var matchedSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStart == targetYearStart);

            if (matchedSchoolYear == null)
                matchedSchoolYear = await _context.SchoolYears
                    .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

            var fund = new OtherFund
            {
                Source       = request.Source,
                Description  = request.Description,
                Category     = request.Category,
                Amount       = request.Amount,
                ReceivedBy   = receivedBy,
                ReceivedDate = receivedDate,
                SchoolYearId = matchedSchoolYear?.SchoolYearId
            };

            _context.OtherFunds.Add(fund);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Fund recorded successfully.", fundId = fund.FundId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    /// <summary>
    /// Chronological (date-aware) spending headroom: the lowest the running balance
    /// reaches on <paramref name="onOrAfter"/> or any later date, computed from all
    /// income (org-fee payments + other funds) minus all expenses, ordered by date.
    /// An expense dated on that day must not exceed this amount, or the balance would
    /// go negative at some point from that date onward. Pass <paramref name="excludeExpenseId"/>
    /// when editing so the expense isn't counted against itself.
    /// </summary>
    private async Task<decimal> GetMinBalanceOnOrAfterAsync(DateTime onOrAfter, int? excludeExpenseId = null)
    {
        var day = onOrAfter.Date;

        var payments = await _context.OrgFeePayments.Select(p => new { p.PaymentDate, p.Amount }).ToListAsync();
        var funds    = await _context.OtherFunds.Select(f => new { f.ReceivedDate, f.Amount }).ToListAsync();
        var expenses = await _context.Expenses
            .Where(e => excludeExpenseId == null || e.ExpenseId != excludeExpenseId)
            .Select(e => new { e.ExpenseDate, e.Amount })
            .ToListAsync();

        // Signed events on a daily timeline: income adds, expenses subtract.
        var events = new List<(DateTime date, decimal amt)>();
        events.AddRange(payments.Select(p => (p.PaymentDate.Date,  p.Amount)));
        events.AddRange(funds.Select(f    => (f.ReceivedDate.Date, f.Amount)));
        events.AddRange(expenses.Select(e => (e.ExpenseDate.Date, -e.Amount)));

        // The running balance only changes on event dates. Its minimum from `day`
        // onward is the value at `day` and at each later event date — evaluate those.
        var points = events.Where(e => e.date > day).Select(e => e.date).Append(day).Distinct();

        var min = decimal.MaxValue;
        foreach (var t in points)
        {
            var bal = events.Where(e => e.date <= t).Sum(e => e.amt);
            if (bal < min) min = bal;
        }
        return min == decimal.MaxValue ? 0m : min;
    }

    [HttpPost]
    public async Task<IActionResult> AddExpense(
        string? description,
        decimal amount,
        DateTime? expenseDate,
        List<IFormFile>? images)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var accountIdStr = HttpContext.Session.GetString("AccountId");
            if (!int.TryParse(accountIdStr, out var recordedBy))
                return Json(new { success = false, message = "Invalid session." });

            if (amount <= 0)
                return Json(new { success = false, message = "Expense amount must be greater than zero." });

            var localExpenseDate = expenseDate?.ToLocalTime() ?? DateTime.Now;

            // Enforce a chronological (date-aware) spending limit: this expense must not
            // drive the running balance negative on its date or at any later point.
            var availableOnDate = await GetMinBalanceOnOrAfterAsync(localExpenseDate);
            if (amount > availableOnDate)
                return Json(new
                {
                    success = false,
                    message = $"Recording ₱{amount:N2} on {localExpenseDate:MMM d, yyyy} would make the balance go negative. The available balance on or after that date is ₱{availableOnDate:N2}."
                });

            // Validate any uploaded receipt images BEFORE saving anything.
            var extByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["image/jpeg"] = ".jpg",
                ["image/png"]  = ".png",
                ["image/webp"] = ".webp"
            };
            var pendingImages = (images ?? new List<IFormFile>()).Where(f => f != null && f.Length > 0).ToList();
            foreach (var file in pendingImages)
            {
                if (file.ContentType == null || !extByType.ContainsKey(file.ContentType))
                    return Json(new { success = false, message = "Only JPG, PNG, or WEBP receipt images are allowed." });
                if (file.Length > 5 * 1024 * 1024)
                    return Json(new { success = false, message = "Each receipt image must be 5MB or smaller." });
            }

            // Find the school year that matches the expense date
            // School years run August-June, so Jan-July belongs to previous year_start
            var targetYearStart = localExpenseDate.Month >= 8 ? localExpenseDate.Year : localExpenseDate.Year - 1;
            var matchedSchoolYear = await _context.SchoolYears
                .FirstOrDefaultAsync(sy => sy.YearStart == targetYearStart);

            // Fall back to current if no match found
            if (matchedSchoolYear == null)
                matchedSchoolYear = await _context.SchoolYears
                    .FirstOrDefaultAsync(sy => sy.YearStatus == YearStatus.Current);

            var expense = new Expense
            {
                Description  = description,
                Amount       = amount,
                RecordedBy   = recordedBy,
                ExpenseDate  = localExpenseDate,
                SchoolYearId = matchedSchoolYear?.SchoolYearId
            };

            // Save the validated receipt images to disk and attach their paths.
            if (pendingImages.Count > 0)
            {
                var expenseDir = Path.Combine(_env.WebRootPath, "uploads", "expenses");
                Directory.CreateDirectory(expenseDir);

                foreach (var file in pendingImages)
                {
                    var ext      = extByType[file.ContentType!];
                    var fileName = $"exp_{Guid.NewGuid():N}{ext}";
                    var fullPath = Path.Combine(expenseDir, fileName);
                    using (var stream = new FileStream(fullPath, FileMode.Create))
                        await file.CopyToAsync(stream);

                    expense.Images.Add(new ExpenseImage { ImagePath = $"/uploads/expenses/{fileName}" });
                }
            }

            _context.Expenses.Add(expense);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Expense recorded successfully.", expenseId = expense.ExpenseId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteOrgFeePayment([FromBody] DeleteTransactionRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var payment = await _context.OrgFeePayments
                .Include(p => p.Receipts)
                .FirstOrDefaultAsync(p => p.PaymentId == request.Id);

            if (payment == null)
                return Json(new { success = false, message = "Payment not found." });

            if (payment.Receipts.Any())
                _context.Receipts.RemoveRange(payment.Receipts);

            _context.OrgFeePayments.Remove(payment);
            await _context.SaveChangesAsync();
            await _sse.BroadcastAsync("payments-changed");

            return Json(new { success = true, message = "Payment deleted successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteOtherFund([FromBody] DeleteTransactionRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var fund = await _context.OtherFunds.FindAsync(request.Id);
            if (fund == null)
                return Json(new { success = false, message = "Fund not found." });

            _context.OtherFunds.Remove(fund);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Fund deleted successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> UpdateOtherFund([FromBody] UpdateOtherFundRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var fund = await _context.OtherFunds.FindAsync(request.Id);
            if (fund == null)
                return Json(new { success = false, message = "Fund not found." });

            fund.Source      = request.Source;
            fund.Description = request.Description;
            fund.Category    = request.Category;
            fund.Amount      = request.Amount;
            fund.ReceivedDate = request.ReceivedDate ?? fund.ReceivedDate;

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Fund updated successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> UpdateOrgFeePayment([FromBody] UpdateOrgFeePaymentRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var payment = await _context.OrgFeePayments
                .Include(p => p.Receipts)
                .FirstOrDefaultAsync(p => p.PaymentId == request.Id);

            if (payment == null)
                return Json(new { success = false, message = "Payment not found." });

            // Reject a receipt number already used by a DIFFERENT payment (re-saving this
            // payment's own number is fine), so the update doesn't fail on uq_receipt_number.
            if (!string.IsNullOrWhiteSpace(request.ReceiptNumber)
                && await _context.Receipts.AnyAsync(r => r.ReceiptNumber == request.ReceiptNumber
                                                      && r.PaymentId != payment.PaymentId))
            {
                return Json(new { success = false, message = $"Receipt number \"{request.ReceiptNumber}\" has already been used. Please enter a different one." });
            }

            payment.Amount = request.Amount;
            payment.PaymentStatus = request.Amount >= request.FullAmount
                ? PaymentStatus.Paid
                : PaymentStatus.Partial;

            if (!string.IsNullOrWhiteSpace(request.ReceiptNumber))
            {
                var receipt = payment.Receipts.FirstOrDefault();
                if (receipt != null)
                    receipt.ReceiptNumber = request.ReceiptNumber;
                else
                {
                    var accountIdStr = HttpContext.Session.GetString("AccountId");
                    int.TryParse(accountIdStr, out var issuedBy);
                    _context.Receipts.Add(new Receipt
                    {
                        ReceiptNumber = request.ReceiptNumber,
                        PaymentId     = payment.PaymentId,
                        IssuedBy      = issuedBy
                    });
                }
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Payment updated successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteExpense([FromBody] DeleteTransactionRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var expense = await _context.Expenses.FindAsync(request.Id);
            if (expense == null)
                return Json(new { success = false, message = "Expense not found." });

            _context.Expenses.Remove(expense);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Expense deleted successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }       
    }

    [HttpPost]
    public async Task<IActionResult> UpdateExpense([FromBody] UpdateExpenseRequest request)
    {
        var guard = RequireAnyRole("Treasurer", "Admin");
        if (guard != null) return guard;

        try
        {
            var expense = await _context.Expenses.FindAsync(request.Id);
            if (expense == null)
                return Json(new { success = false, message = "Expense not found." });

            if (request.Amount <= 0)
                return Json(new { success = false, message = "Expense amount must be greater than zero." });

            // Re-validate against the chronological balance, excluding this expense so it
            // isn't counted against itself. Uses the new date/amount being applied.
            var newDate = (request.ExpenseDate ?? expense.ExpenseDate).ToLocalTime();
            var availableOnDate = await GetMinBalanceOnOrAfterAsync(newDate, expense.ExpenseId);
            if (request.Amount > availableOnDate)
                return Json(new
                {
                    success = false,
                    message = $"Updating this expense to ₱{request.Amount:N2} on {newDate:MMM d, yyyy} would make the balance go negative. The available balance on or after that date is ₱{availableOnDate:N2}."
                });

            expense.Description = request.Description;
            expense.Amount      = request.Amount;
            expense.ExpenseDate = newDate;

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Expense updated successfully." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            return Json(new { success = false, message = "An error occurred. Please try again." });
        }
    }

}
