using MyMvcApp.Models;

namespace MyMvcApp.Services
{
    public interface IAuthService
    {
        Task<AuthResult> AuthenticateAsync(string email, string password, UserRole role);
        Task<AuthResult> AuthenticateBySchoolIdAsync(string schoolId, string password, UserRole role);
        Task<AuthResult> AuthenticateByStudentIdAsync(string studentId, string password, UserRole role);
        Task<RegistrationResult> RegisterAccountAsync(RegistrationRequest request);
    }

    public class AuthResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public Account? Account { get; set; }
        public User? User { get; set; }
    }

    public class RegistrationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public Account? Account { get; set; }
        public User? User { get; set; }
    }

    public class RegistrationRequest
    {
        public string SchoolId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public UserRole Role { get; set; }
        
        // User specific fields (for all roles)
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? MiddleName { get; set; }
        
        // Student specific fields
        public string? CourseCode { get; set; }
        public int? SchoolYearId { get; set; }
        public Semester? SemesterEntered { get; set; }
        public int? YearLevel { get; set; }
        public string? Section { get; set; }
        public string? StudentId { get; set; }
    }
}
