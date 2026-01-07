using BlazorBootstrap;
using itasa_app.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using System.Data;

namespace itasa_app.Components.Pages
{
    public partial class BorrowPage : ComponentBase
    {
        [Inject] public IWebHostEnvironment env { get; set; } = default!;
        [Inject] public NavigationManager NavManager { get; set; } = default!;

        // --- Data Lists ---
        public List<BorrowModels> borrowList = new List<BorrowModels>();
        public List<DepartmentModels> departmentList = new List<DepartmentModels>();
        public List<ItemModels> itemList = new List<ItemModels>();

        // --- UI Control Variables ---
        // ✅ ประกาศตัวแปร Grid เพื่อสั่ง Refresh ข้อมูล
        private Grid<BorrowModels> borrowGrid = default!;
        private Modal returnModal = default!;

        public BorrowModels editingBorrow = new BorrowModels();
        public string errorMessage = "";

        // --- File Upload ---
        private IBrowserFile? selectedFile;
        private string previewImage = "";
        private const long MaxFileSize = 5 * 1024 * 1024; // 5MB

        // --- Search & Filter ---
        // ❌ filteredBorrowList ไม่ต้องใช้แล้ว เพราะ Grid จัดการเองผ่าน DataProvider
        // public List<BorrowModels> filteredBorrowList = new List<BorrowModels>(); 

        public string searchText = "";
        public int filterStatus = 0;

        // --- Forms & Models ---
        public BorrowModels newBorrow = new BorrowModels();
        public List<ItemModels> cartList = new List<ItemModels>(); // ตะกร้าสินค้า
        public int tempSelectedItemId { get; set; }
        public string tempSelectedBarcode { get; set; } = "";

        protected override async Task OnInitializedAsync()
        {
            newBorrow.BorrowDate = DateTime.Now;
            newBorrow.DueDate = DateTime.Now;

            LoadDepartments();
            LoadItems();
            LoadBorrowData(); // โหลดข้อมูลตั้งต้นเข้า borrowList
        }

        // --- Logic: เลือกของใส่ตะกร้า (Cart) ---
        public void OnItemSelected(int selectedId)
        {
            tempSelectedItemId = selectedId;
            var item = itemList.FirstOrDefault(i => i.Id == selectedId);
            tempSelectedBarcode = item != null ? item.ItemBarcode : "";
            StateHasChanged();
        }

        public void AddToCart()
        {
            if (tempSelectedItemId == 0) return;
            if (cartList.Any(x => x.Id == tempSelectedItemId)) return;

            var item = itemList.FirstOrDefault(i => i.Id == tempSelectedItemId);
            if (item != null) cartList.Add(item);

            // Reset selection
            tempSelectedItemId = 0;
            tempSelectedBarcode = "";
        }

        public void RemoveFromCart(ItemModels item)
        {
            cartList.Remove(item);
        }

        // --- Logic: บันทึกการยืม (Save Borrow) ---
        public async Task SaveBorrow()
        {
            if (cartList.Count == 0)
            {
                errorMessage = "กรุณาเลือกอุปกรณ์อย่างน้อย 1 ชิ้น";
                return;
            }

            try
            {
                var dept = departmentList.FirstOrDefault(d => d.DepartmentCode == newBorrow.DepartmentCode);
                string deptName = dept != null ? dept.DepartmentName : "";

                // Upload Image
                string? uploadedPath = null;
                if (selectedFile != null)
                {
                    var newFileName = $"{Guid.NewGuid()}{Path.GetExtension(selectedFile.Name)}";
                    var folderPath = Path.Combine(env.WebRootPath, "uploads");
                    if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                    var fullPath = Path.Combine(folderPath, newFileName);
                    await using FileStream fs = new(fullPath, FileMode.Create);
                    await selectedFile.OpenReadStream(MaxFileSize).CopyToAsync(fs);
                    uploadedPath = $"/uploads/{newFileName}";
                }

                // Database Transaction
                using (var conn = new Myconnection().GetConnection())
                {
                    if (conn.State != ConnectionState.Open) await conn.OpenAsync();

                    using (var trans = await conn.BeginTransactionAsync())
                    {
                        try
                        {
                            foreach (var item in cartList)
                            {
                                // 1. Insert Borrow Record
                                using (var cmd = conn.CreateCommand())
                                {
                                    cmd.Transaction = trans;

                                    // ⚠️ ถ้า Database ยังไม่มี column "DepartmentCode" บรรทัดนี้จะ Error 42703
                                    // ต้องไป Alter Table ใน pgAdmin ก่อนครับ
                                    cmd.CommandText = @"
                                        INSERT INTO public.borrow_tb
                                        (""BorrowerName"", ""Department"", ""DepartmentCode"", ""Equipment"", ""Barcode"", 
                                         ""BorrowDate"", ""DueDate"", ""Status"", ""ItemId"", ""PhotoPath"")
                                        VALUES
                                        (@name, @deptName, @deptCode, @equip, @barcode, @bDate, @dDate, @status, @itemId, @photo)";

                                    cmd.Parameters.AddWithValue("name", newBorrow.BorrowerName);
                                    cmd.Parameters.AddWithValue("deptName", deptName);
                                    cmd.Parameters.AddWithValue("deptCode", newBorrow.DepartmentCode ?? "");
                                    cmd.Parameters.AddWithValue("equip", item.ItemName);
                                    cmd.Parameters.AddWithValue("barcode", item.ItemBarcode);
                                    cmd.Parameters.AddWithValue("bDate", (object)newBorrow.BorrowDate ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("dDate", (object)newBorrow.DueDate ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("status", (short)BorrowStatus.Borrowed);
                                    cmd.Parameters.AddWithValue("itemId", item.Id);
                                    cmd.Parameters.AddWithValue("photo", uploadedPath ?? (object)DBNull.Value);

                                    await cmd.ExecuteNonQueryAsync();
                                }

                                // 2. Update Item Status -> Borrowed (2)
                                using (var updateCmd = conn.CreateCommand())
                                {
                                    updateCmd.Transaction = trans;
                                    updateCmd.CommandText = @"UPDATE public.item_tb SET ""ItemStatus"" = 2 WHERE ""Id"" = @id";
                                    updateCmd.Parameters.AddWithValue("id", item.Id);
                                    await updateCmd.ExecuteNonQueryAsync();
                                }
                            }
                            await trans.CommitAsync();
                        }
                        catch
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }

                // Cleanup & Refresh
                cartList.Clear();
                newBorrow = new BorrowModels { BorrowDate = DateTime.Now, DueDate = DateTime.Now };
                selectedFile = null;
                previewImage = "";
                errorMessage = "";

                LoadBorrowData(); // โหลดข้อมูลใหม่เข้า List
                LoadItems();      // โหลดสถานะของใหม่ (ของที่ถูกยืมจะหายไปจาก dropdown)

                // ✅ สั่ง Grid ให้แสดงข้อมูลใหม่
                await borrowGrid.RefreshDataAsync();
            }
            catch (Exception ex)
            {
                errorMessage = "บันทึกไม่สำเร็จ: " + ex.Message;
            }
        }

        // --- Logic: การคืนของ (Return) ---
        public async Task OpenReturnModal(BorrowModels b)
        {
            editingBorrow = new BorrowModels
            {
                Id = b.Id,
                BorrowerName = b.BorrowerName,
                Equipment = b.Equipment,
                ItemId = b.ItemId,
                Status = b.Status,
                ReturnDate = b.ReturnDate ?? DateTime.Now
            };
            await returnModal.ShowAsync();
        }

        public async Task CloseReturnModal()
        {
            await returnModal.HideAsync();
        }

        public async Task SaveReturn()
        {
            try
            {
                using (var conn = new Myconnection().GetConnection())
                {
                    // 1. Update Borrow Status
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            UPDATE public.borrow_tb 
                            SET ""Status"" = @status, 
                                ""ReturnDate"" = @rDate
                            WHERE ""Id"" = @id";

                        cmd.Parameters.AddWithValue("status", (short)editingBorrow.Status);
                        cmd.Parameters.AddWithValue("id", editingBorrow.Id);

                        if (editingBorrow.Status == BorrowStatus.Return)
                            cmd.Parameters.AddWithValue("rDate", editingBorrow.ReturnDate ?? DateTime.Now);
                        else
                            cmd.Parameters.AddWithValue("rDate", DBNull.Value);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    // 2. ถ้าคืนของ -> ปลดล็อค Item เป็น Ready (1)
                    if (editingBorrow.Status == BorrowStatus.Return && editingBorrow.ItemId != 0)
                    {
                        using (var itemCmd = conn.CreateCommand())
                        {
                            itemCmd.CommandText = @"UPDATE public.item_tb SET ""ItemStatus"" = 1 WHERE ""Id"" = @itemId";
                            itemCmd.Parameters.AddWithValue("itemId", editingBorrow.ItemId);
                            await itemCmd.ExecuteNonQueryAsync();
                        }
                    }
                }

                await CloseReturnModal();
                LoadBorrowData();
                LoadItems();

                // ✅ สั่ง Refresh Grid
                await borrowGrid.RefreshDataAsync();
            }
            catch (Exception ex)
            {
                errorMessage = "อัปเดตสถานะไม่สำเร็จ: " + ex.Message;
            }
        }

        // --- Helper Methods ---
        private async Task LoadFiles(InputFileChangeEventArgs e)
        {
            try
            {
                selectedFile = e.File;
                var format = "image/png";
                var resizedImage = await selectedFile.RequestImageFileAsync(format, 300, 300);
                var buffer = new byte[resizedImage.Size];
                await resizedImage.OpenReadStream().ReadAsync(buffer);
                var imageData = Convert.ToBase64String(buffer);
                previewImage = $"data:{format};base64,{imageData}";
            }
            catch (Exception ex) { errorMessage = "โหลดรูปไม่สำเร็จ: " + ex.Message; }
        }

        // ❌ เอาฟังก์ชัน FilterData แบบเก่าออก (เพราะใช้ DataProvider แทนแล้ว)
        // public void FilterData() { ... }

        // --- Grid Data Provider (หัวใจสำคัญของ Grid) ---
        private async Task<GridDataProviderResult<BorrowModels>> BorrowDataProvider(GridDataProviderRequest<BorrowModels> request)
        {
            // ถ้า borrowList ยังว่าง ให้โหลดข้อมูลจาก DB ก่อน
            if (borrowList == null || borrowList.Count == 0) LoadBorrowData();

            // เริ่ม Query จาก borrowList ที่มีอยู่ใน Memory
            var result = borrowList.AsEnumerable();

            // 1. กรองสถานะ
            if (filterStatus != 0)
            {
                result = result.Where(x => (int)x.Status == filterStatus);
            }

            // 2. กรองคำค้นหา
            if (!string.IsNullOrEmpty(searchText))
            {
                result = result.Where(x =>
                    (x.BorrowerName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (x.Equipment?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (x.Barcode?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)
                );
            }

            // ส่งผลลัพธ์ให้ Grid ไปจัดการ Sort/Page ต่อเอง
            return await Task.FromResult(request.ApplyTo(result));
        }

        // ปุ่มค้นหา -> สั่ง Grid ให้โหลดข้อมูลใหม่ (ซึ่งจะวิ่งไปเรียก BorrowDataProvider)
        private async Task SearchBorrow()
        {
            await borrowGrid.RefreshDataAsync();
        }

        // ปุ่ม Reset
        private async Task ClearSearch()
        {
            searchText = "";
            filterStatus = 0;
            await borrowGrid.RefreshDataAsync();
        }

        // --- Database Loaders ---
        private void LoadDepartments()
        {
            try
            {
                departmentList.Clear();
                using (var conn = new Myconnection().GetConnection())
                {
                    using (var cmd = new NpgsqlCommand(@"SELECT ""Id"", ""DepartmentName"", ""DepartmentCode"" FROM public.department_tb", conn))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            departmentList.Add(new DepartmentModels
                            {
                                Id = r["Id"] != DBNull.Value ? Convert.ToInt32(r["Id"]) : 0,
                                DepartmentName = r["DepartmentName"]?.ToString() ?? "-",
                                DepartmentCode = r["DepartmentCode"]?.ToString() ?? "-"
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private void LoadItems()
        {
            try
            {
                itemList.Clear();
                using (var conn = new Myconnection().GetConnection())
                {
                    using (var cmd = new NpgsqlCommand(@"SELECT ""Id"", ""ItemName"", ""ItemBarcode"", ""ItemStatus"" FROM public.item_tb", conn))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            itemList.Add(new ItemModels
                            {
                                Id = r["Id"] != DBNull.Value ? Convert.ToInt32(r["Id"]) : 0,
                                ItemName = r["ItemName"]?.ToString() ?? "",
                                ItemBarcode = r["ItemBarcode"]?.ToString() ?? "",
                                ItemStatus = r["ItemStatus"] != DBNull.Value ? (ItemStatus)Convert.ToInt32(r["ItemStatus"]) : (ItemStatus)1
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private void LoadBorrowData()
        {
            try
            {
                borrowList.Clear();
                errorMessage = "";
                using (var conn = new Myconnection().GetConnection())
                {
                    // ⚠️ ระวัง: เช็คชื่อคอลัมน์ DepartmentCode ใน DB ให้ชัวร์ว่ามีแล้ว
                    using (var cmd = new NpgsqlCommand(@"
                        SELECT ""Id"", ""BorrowerName"", ""Department"", ""DepartmentCode"", ""Equipment"",
                               ""Barcode"", ""BorrowDate"", ""DueDate"", ""ReturnDate"",
                               ""Status"", ""PhotoPath"", ""ItemId""
                        FROM public.borrow_tb 
                        ORDER BY ""Id"" DESC", conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var b = new BorrowModels();
                            b.Id = reader["Id"] != DBNull.Value ? Convert.ToInt32(reader["Id"]) : 0;
                            b.BorrowerName = reader["BorrowerName"]?.ToString() ?? "";
                            b.Department = reader["Department"]?.ToString() ?? "";
                            b.DepartmentCode = reader["DepartmentCode"]?.ToString() ?? "";
                            b.Equipment = reader["Equipment"]?.ToString() ?? "";
                            b.Barcode = reader["Barcode"]?.ToString() ?? "";
                            b.PhotoPath = reader["PhotoPath"]?.ToString();

                            b.BorrowDate = MapDate(reader["BorrowDate"]);
                            b.DueDate = MapDate(reader["DueDate"]);
                            b.ReturnDate = MapDate(reader["ReturnDate"]);

                            if (reader["Status"] != DBNull.Value)
                                b.Status = (BorrowStatus)Convert.ToInt16(reader["Status"]);

                            b.ItemId = reader["ItemId"] != DBNull.Value ? Convert.ToInt32(reader["ItemId"]) : 0;

                            borrowList.Add(b);
                        }
                    }
                }
            }
            catch (Exception ex) { errorMessage = "โหลดข้อมูลไม่สำเร็จ: " + ex.Message; }
        }
        private async Task OnStatusChanged(ChangeEventArgs e)
        {
            // 1. แปลงค่าที่เลือกเป็น int
            if (int.TryParse(e.Value?.ToString(), out int result))
            {
                filterStatus = result;
            }

            // 2. สั่งให้ Grid โหลดข้อมูลใหม่ทันที
            await borrowGrid.RefreshDataAsync();
        }
        private async Task OnSearchTextChanged(ChangeEventArgs e)
        {
            searchText = e.Value?.ToString() ?? "";
            await borrowGrid.RefreshDataAsync();
        }
        private DateTime? MapDate(object dbValue)
        {
            if (dbValue == DBNull.Value || dbValue == null) return null;
            if (dbValue is DateOnly dateOnly) return dateOnly.ToDateTime(TimeOnly.MinValue);
            return Convert.ToDateTime(dbValue);
        }
    }
}