using System.ComponentModel.DataAnnotations;

namespace MyMvcApp.Models
{
    public class RequestedAccountViewModel
    {
        public int AccountId { get; set; }
        public string? SchoolId { get; set; }
        public string Fullname { get; set; } = string.Empty;
        public string? CourseCode { get; set; }
        public string? YearLevel { get; set; }
        public string? Section { get; set; }
        public string? Role { get; set; }
        public string? AvatarPath { get; set; }

        public DateTime CreatedAt { get; set; }
        public RequestStatus Status { get; set; }
    }
}
