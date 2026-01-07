using System.ComponentModel.DataAnnotations;

namespace itasa_app.Models
{
    public class DepartmentModels
    {
 
            public int Id { get; set; }
           
            [Required(ErrorMessage = "กรุณากรอก Code ")]
            [StringLength(100)]
            public string DepartmentCode { get; set; }

            [Required(ErrorMessage = "กรุณากรอกชื่อแผนก")]
            [StringLength(100)]
            public string DepartmentName { get; set; }

        
    }
}
