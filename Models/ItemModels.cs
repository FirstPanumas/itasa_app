using System.ComponentModel.DataAnnotations;

namespace itasa_app.Models
{
    public enum ItemStatus : short
    {
        Ready = 1, // พร้อมใช้งาน
        Borrowed = 2, // กำลังยืม
        Lost = 3, // สูญหาย
        Damaged = 4  // ชำรุด
    }
    public class ItemModels
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "กรุณาเพิ่มข้อมูล ItemName")]
        [StringLength(100)]
        public string ItemName { get; set; }

        [Required(ErrorMessage = "กรุณาเพิ่มข้อมูล Description")]
        [StringLength(100)]
        public string Description { get; set; }

        [Required(ErrorMessage = "กรุณาเพิ่มข้อมูล ItemBarcode")]
        [StringLength(100)]
        public string ItemBarcode { get; set; }
        public ItemStatus ItemStatus { get; set; } = ItemStatus.Ready;
        public string? ItemImage { get; set; }
    }
}
