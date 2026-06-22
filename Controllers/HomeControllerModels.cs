using System;
using System.Collections.Generic;
using MyMvcApp.Models;

namespace MyMvcApp.Controllers;

// ----------------------------------------------------------------
// REQUEST / RESPONSE MODELS
// ----------------------------------------------------------------

public class CreateReportRequest
{
    public string ReportType  { get; set; } = string.Empty;
    public string Title       { get; set; } = string.Empty;
    public int?   SchoolYearId { get; set; }
    public string? Semester   { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public List<int> ExpenseIds { get; set; } = new();
    public List<int> FundIds    { get; set; } = new();
}

public class DeleteReportRequest
{
    public int ReportId { get; set; }
}

public class LoginRequest
{
    public string  SchoolId  { get; set; } = string.Empty;
    public string  Password  { get; set; } = string.Empty;
    public string? Email     { get; set; }
    public string? StudentId { get; set; }
    public string? Role      { get; set; }
}

public class ForgotPasswordRequest
{
    public string StudentId { get; set; } = string.Empty;
}

public class VerifyCodeRequest
{
    public string StudentId { get; set; } = string.Empty;
    public string Code      { get; set; } = string.Empty;
}

public class DeactivateRequest
{
    public int AccountId { get; set; }
}

public class ResetPasswordRequest
{
    public string Token       { get; set; } = string.Empty;
    public string StudentId   { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword     { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class ResetPasswordViewModel
{
    public string Token     { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
}

public class UpdateAccountStatusRequest
{
    public int           AccountId { get; set; }
    public RequestStatus Status    { get; set; }
}

public class UpdateStudentRequest
{
    public int     AccountId      { get; set; }
    public string? FirstName      { get; set; }
    public string? LastName       { get; set; }
    public string? MiddleName     { get; set; }
    public string? Email          { get; set; }
    public int     CourseId       { get; set; }
    public int?    YearLevel      { get; set; }
    public string? Section        { get; set; }
    public string? AcademicStatus { get; set; }  // add this
}

public class UpdateProfessorRequest
{
    public int     AccountId  { get; set; }
    public string? FirstName  { get; set; }
    public string? LastName   { get; set; }
    public string? MiddleName { get; set; }
    public string? Email      { get; set; }
    public string? SchoolId   { get; set; }
}

public class ChangeRoleRequest
{
    public int    AccountId { get; set; }
    public string Role      { get; set; } = string.Empty;
}

public class AddSchoolYearRequest
{
    public int       YearStart      { get; set; }
    public int       YearEnd        { get; set; }
    public DateTime? FirstSemStart  { get; set; }
    public DateTime? FirstSemEnd    { get; set; }
    public DateTime? SecondSemStart { get; set; }
    public DateTime? SecondSemEnd   { get; set; }
}

public class DeleteSchoolYearRequest
{
    public int SchoolYearId { get; set; }
}

public class SetSchoolYearStatusRequest
{
    public int    SchoolYearId { get; set; }
    public string Status       { get; set; } = string.Empty;
}

public class UpdateSchoolYearDatesRequest
{
    public int       SchoolYearId   { get; set; }
    public DateTime? FirstSemStart  { get; set; }
    public DateTime? FirstSemEnd    { get; set; }
    public DateTime? SecondSemStart { get; set; }
    public DateTime? SecondSemEnd   { get; set; }
}

public class AddCourseRequest
{
    public string  CourseCode { get; set; } = string.Empty;
    public string? CourseName { get; set; }
}

public class DeleteCourseRequest
{
    public int CourseId { get; set; }
}

public class SetFeeAmountRequest
{
    public int     SchoolYearId { get; set; }
    public string  Semester     { get; set; } = string.Empty;
    public decimal Amount       { get; set; }
}

public class DeleteFeeRequest
{
    public int FullAmountId { get; set; }
}

public class AddOrgFeePaymentRequest
{
    public int     UserId        { get; set; }
    public decimal Amount        { get; set; }
    public string? Semester      { get; set; }  // add this
    public int     FullAmountId  { get; set; }
    public string? ReceiptNumber { get; set; }
}


    public class AddOtherFundRequest
    {
        public string? Source { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
    public decimal Amount { get; set; }
    public DateTime? ReceivedDate { get; set; }
}

public class AddExpenseRequest
{
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public DateTime? ExpenseDate { get; set; }
}

public class DeleteTransactionRequest
{
    public int Id { get; set; }
}

public class UpdateOtherFundRequest
{
    public int       Id           { get; set; }
    public string?   Source       { get; set; }
    public string?   Description  { get; set; }
    public string?   Category     { get; set; }
    public decimal   Amount       { get; set; }
    public DateTime? ReceivedDate { get; set; }
}

public class UpdateOrgFeePaymentRequest
{
    public int     Id            { get; set; }
    public decimal Amount        { get; set; }
    public decimal FullAmount    { get; set; }
    public string? ReceiptNumber { get; set; }
}

public class UpdateExpenseRequest
{
    public int       Id          { get; set; }
    public string?   Description { get; set; }
    public decimal   Amount      { get; set; }
    public DateTime? ExpenseDate { get; set; }
}

public class SaveSignatureRequest
{
    public string SignatureData { get; set; } = string.Empty;
}

public record SendOtpRequest(string Email);
public record VerifyOtpRequest(string Email, string Code);

public class EditFeeRequest
{
    public int     FullAmountId { get; set; }
    public decimal Amount       { get; set; }
}

public class SetFeeStatusRequest
{
    public int    FullAmountId { get; set; }
    public string Status       { get; set; } = string.Empty;
}

public class SetStudentPaymentStartRequest
{
    public int    UserId       { get; set; }
    public int    SchoolYearId { get; set; }
    public string Semester     { get; set; } = string.Empty;
}

public class StudentExemptionRequest
{
    public int    UserId       { get; set; }
    public int    SchoolYearId { get; set; }
    public string Semester     { get; set; } = string.Empty;
}

public class RemoveStudentExemptionRequest
{
    public int ExemptionId { get; set; }
}

public class ReturnFromLeaveRequest
{
    // Only the student is needed; the server derives the last attended term and
    // the leave gap to exempt automatically. See HomeController.ReturnFromLeave.
    public int UserId { get; set; }
}
