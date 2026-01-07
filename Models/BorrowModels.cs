using System.ComponentModel.DataAnnotations;

namespace itasa_app.Models
{
    public enum BorrowStatus : short
    {
        Return = 1, // ส่งคืน
        Borrowed = 2, // กำลังยืม
    }

    public class BorrowModels
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "กรุณากรอกชื่อผู้ยืม")][StringLength(100)] public string BorrowerName { get; set; } = "";
        public string Department { get; set; } = "";
        public string Equipment { get; set; } = "";
        public string Barcode { get; set; }

        [Required(ErrorMessage = "กรุณาเลือกวันที่ยืม")]
        public DateTime? BorrowDate { get; set; } = DateTime.Today;
        public DateTime? DueDate { get; set; } = DateTime.Today;
        public DateTime? ReturnDate { get; set; }

        public BorrowStatus Status { get; set; } = BorrowStatus.Borrowed;
        public string? PhotoPath { get; set; }

        // ✅ เพิ่มการตรวจสอบที่ ID แทน (ต้องเลือกค่าตั้งแต่ 1 ขึ้นไป)
        [Required(ErrorMessage = "กรุณาเลือกแผนก")]
        public string DepartmentCode { get; set; } = "";
       

        public int ItemId { get; set; }
    }
}
