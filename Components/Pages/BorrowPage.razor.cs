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
        private bool isUploading = false;

        // --- UI Control Variables ---
        private Grid<BorrowModels> borrowGrid = default!;
        private Modal returnModal = default!;

        // ตัวแปรสำหรับ Modal อัปโหลดรูป และจำ ID
        private Modal uploadModal = default!;
        private List<int> savedTransactionIds = new List<int>();

        public BorrowModels editingBorrow = new BorrowModels();
        public string errorMessage = "";

        // --- File Upload (✅ แก้ไขให้เหมือน ItemPage) ---
        // ไม่ต้องเก็บ IBrowserFile แล้ว เก็บแค่ Path ที่อัปโหลดเสร็จแล้ว
        private string uploadedImagePath = "";
        private const long MaxFileSize = 10 * 1024 * 1024; // 10MB

        // --- Search & Filter ---
        public string searchText = "";
        public int filterStatus = 0;

        // --- Forms & Models ---
        public BorrowModels newBorrow = new BorrowModels();
        public List<ItemModels> cartList = new List<ItemModels>();

        // --- AutoComplete Variables ---
        private ItemModels tempSelectedItem;
        public int tempSelectedItemId { get; set; }
        public string tempSelectedBarcode { get; set; } = "";
        private string tempSearchText = "";

        protected override async Task OnInitializedAsync()
        {
            newBorrow.BorrowDate = DateTime.Now;
            newBorrow.DueDate = DateTime.Now;

            LoadDepartments();
            LoadItems();
            LoadBorrowData();
        }

        // --- Logic: เลือกของใส่ตะกร้า ---
        public void AddToCart()
        {
            if (tempSelectedItem == null) return;

            if ((int)tempSelectedItem.ItemStatus == 2)
            {
                errorMessage = $"อุปกรณ์ '{tempSelectedItem.ItemName}' ถูกยืมไปแล้ว ไม่สามารถยืมซ้ำได้";
                StateHasChanged();
                return;
            }

            if (cartList.Any(x => x.Id == tempSelectedItem.Id)) return;

            cartList.Add(tempSelectedItem);

            tempSelectedItem = null;
            tempSelectedItemId = 0;
            tempSelectedBarcode = "";
            tempSearchText = "";
            errorMessage = "";
        }

        public void RemoveFromCart(ItemModels item)
        {
            cartList.Remove(item);
        }

        // --- Logic: บันทึกข้อมูล (Step 1 - บันทึกรายการยืมก่อน) ---
        public async Task SaveBorrow()
        {
            if (cartList.Count == 0)
            {
                errorMessage = "กรุณาเลือกอุปกรณ์อย่างน้อย 1 ชิ้น";
                return;
            }

            try
            {
                savedTransactionIds.Clear();
                var dept = departmentList.FirstOrDefault(d => d.DepartmentCode == newBorrow.DepartmentCode);
                string deptName = dept != null ? dept.DepartmentName : "";

                using (var conn = new Myconnection().GetConnection())
                {
                    if (conn.State != ConnectionState.Open) await conn.OpenAsync();

                    using (var trans = await conn.BeginTransactionAsync())
                    {
                        try
                        {
                            foreach (var item in cartList)
                            {
                                using (var cmd = conn.CreateCommand())
                                {
                                    cmd.Transaction = trans;
                                    cmd.CommandText = @"
                                        INSERT INTO public.borrow_tb
                                        (""BorrowerName"", ""Department"", ""DepartmentCode"", ""Equipment"", ""Barcode"", 
                                         ""BorrowDate"", ""DueDate"", ""Status"", ""ItemId"") 
                                        VALUES
                                        (@name, @deptName, @deptCode, @equip, @barcode, @bDate, @dDate, @status, @itemId)
                                        RETURNING ""Id""";

                                    cmd.Parameters.AddWithValue("name", newBorrow.BorrowerName);
                                    cmd.Parameters.AddWithValue("deptName", deptName);
                                    cmd.Parameters.AddWithValue("deptCode", newBorrow.DepartmentCode ?? "");
                                    cmd.Parameters.AddWithValue("equip", item.ItemName);
                                    cmd.Parameters.AddWithValue("barcode", item.ItemBarcode);
                                    cmd.Parameters.AddWithValue("bDate", (object)newBorrow.BorrowDate ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("dDate", (object)newBorrow.DueDate ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("status", (short)BorrowStatus.Borrowed);
                                    cmd.Parameters.AddWithValue("itemId", item.Id);

                                    var newIdObj = await cmd.ExecuteScalarAsync();
                                    if (newIdObj != null)
                                    {
                                        savedTransactionIds.Add(Convert.ToInt32(newIdObj));
                                    }
                                }

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

                // ✅ Clear รูปเก่าทิ้ง ก่อนเปิด Modal
                ClearImage();
                await uploadModal.ShowAsync();
            }
            catch (Exception ex)
            {
                errorMessage = "บันทึกข้อมูลไม่สำเร็จ: " + ex.Message;
            }
        }

        // --- ✅ Helper Methods: Upload (แบบ ItemPage) ---
        // 1. เมธอดนี้จะทำงานทันทีที่เลือกไฟล์ (InputFile OnChange)
        private async Task HandleImageUpload(InputFileChangeEventArgs e)
        {
            var file = e.File;
            if (file != null)
            {
                try
                {
                    isUploading = true;
                    StateHasChanged();

                    // สร้างชื่อไฟล์
                    var fileExtension = Path.GetExtension(file.Name);
                    var newFileName = $"{Guid.NewGuid()}{fileExtension}";
                    var uploadFolder = Path.Combine(env.WebRootPath, "uploads");

                    if (!Directory.Exists(uploadFolder)) Directory.CreateDirectory(uploadFolder);

                    var filePath = Path.Combine(uploadFolder, newFileName);

                    // ✅ บันทึกลง Disk ทันที
                    await using (var fs = new FileStream(filePath, FileMode.Create))
                    {
                        // ถ้าไฟล์ใหญ่ หรือต้องการ Resize สามารถใช้ logic RequestImageFileAsync ตรงนี้ได้
                        // ตัวอย่าง: ถ้าต้องการ Resize ก่อนเซฟ
                        // var resizedFile = await file.RequestImageFileAsync(file.ContentType, 800, 800);
                        // await resizedFile.OpenReadStream(MaxFileSize).CopyToAsync(fs);

                        // บันทึกไฟล์ต้นฉบับ
                        await file.OpenReadStream(MaxFileSize).CopyToAsync(fs);
                    }

                    // ✅ เก็บ Path ไว้เตรียมอัปเดต DB
                    uploadedImagePath = $"/uploads/{newFileName}";
                }
                catch (Exception ex)
                {
                    errorMessage = "อัปโหลดไฟล์ไม่สำเร็จ: " + ex.Message;
                }
                finally
                {
                    isUploading = false;
                    StateHasChanged();
                }
            }
        }

        // 2. เมธอดสำหรับลบรูป (ลบ Path ออกจากตัวแปร)
        private void ClearImage()
        {
            uploadedImagePath = "";
        }

        // --- Logic: อัปเดต Path รูปเข้า DB (Step 2) ---
        // เมธอดนี้จะทำงานเมื่อกดปุ่ม "ยืนยัน/จบงาน" ใน Modal
        public async Task SaveImageToDb()
        {
            // ถ้าไม่มีรายการ transaction หรือ ยังไม่ได้อัปโหลดรูป ให้ข้าม
            if (savedTransactionIds.Count == 0 || string.IsNullOrEmpty(uploadedImagePath))
            {
                await FinishTransaction();
                return;
            }

            try
            {
                using (var conn = new Myconnection().GetConnection())
                {
                    if (conn.State != ConnectionState.Open) await conn.OpenAsync();
                    using (var cmd = conn.CreateCommand())
                    {
                        // อัปเดต PhotoPath ให้กับทุกรายการที่เพิ่งบันทึกไป
                        string idsStr = string.Join(",", savedTransactionIds);
                        cmd.CommandText = $@"UPDATE public.borrow_tb SET ""PhotoPath"" = @path WHERE ""Id"" IN ({idsStr})";
                        cmd.Parameters.AddWithValue("path", uploadedImagePath);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                await FinishTransaction();
            }
            catch (Exception ex)
            {
                errorMessage = "บันทึกรูปภาพลงฐานข้อมูลไม่สำเร็จ: " + ex.Message;
                // ถ้า Error ตรงนี้ อาจจะเลือกที่จะปิด Modal หรือให้ลองใหม่
            }
        }

        public async Task FinishTransaction()
        {
            await uploadModal.HideAsync();

            cartList.Clear();
            newBorrow = new BorrowModels { BorrowDate = DateTime.Now, DueDate = DateTime.Now };
            ClearImage();
            savedTransactionIds.Clear();
            errorMessage = "";

            LoadBorrowData();
            LoadItems();
            await borrowGrid.RefreshDataAsync();
        }

        // --- Mobile View Filter ---
        private IEnumerable<BorrowModels> GetFilteredItemsForMobile()
        {
            var result = borrowList.AsEnumerable();
            if (filterStatus != 0) result = result.Where(x => (int)x.Status == filterStatus);
            if (!string.IsNullOrEmpty(searchText))
            {
                result = result.Where(x =>
                    (x.BorrowerName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (x.Equipment?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (x.Barcode?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)
                );
            }
            return result.OrderByDescending(x => x.Id);
        }

        // --- Logic: Return ---
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

        public async Task CloseReturnModal() => await returnModal.HideAsync();

        public async Task SaveReturn()
        {
            try
            {
                using (var conn = new Myconnection().GetConnection())
                {
                    if (conn.State != ConnectionState.Open) await conn.OpenAsync();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"UPDATE public.borrow_tb SET ""Status"" = @status, ""ReturnDate"" = @rDate WHERE ""Id"" = @id";
                        cmd.Parameters.AddWithValue("status", (short)editingBorrow.Status);
                        cmd.Parameters.AddWithValue("id", editingBorrow.Id);
                        cmd.Parameters.AddWithValue("rDate", editingBorrow.Status == BorrowStatus.Return ? editingBorrow.ReturnDate ?? DateTime.Now : DBNull.Value);
                        await cmd.ExecuteNonQueryAsync();
                    }

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
                await borrowGrid.RefreshDataAsync();
            }
            catch (Exception ex) { errorMessage = "อัปเดตสถานะไม่สำเร็จ: " + ex.Message; }
        }

        // --- Grid & AutoComplete Data Provider ---
        private async Task<GridDataProviderResult<BorrowModels>> BorrowDataProvider(GridDataProviderRequest<BorrowModels> request)
        {
            if (borrowList == null || borrowList.Count == 0) LoadBorrowData();
            var result = borrowList.AsEnumerable();
            if (filterStatus != 0) result = result.Where(x => (int)x.Status == filterStatus);
            if (!string.IsNullOrEmpty(searchText))
            {
                result = result.Where(x =>
                    (x.BorrowerName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (x.Equipment?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (x.Barcode?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)
                );
            }
            return await Task.FromResult(request.ApplyTo(result));
        }

        private async Task SearchBorrow() => await borrowGrid.RefreshDataAsync();
        private async Task ClearSearch() { searchText = ""; filterStatus = 0; await borrowGrid.RefreshDataAsync(); }

        private async Task<AutoCompleteDataProviderResult<ItemModels>> EquipmentDataProvider(AutoCompleteDataProviderRequest<ItemModels> request)
        {
            var query = itemList.Where(x => !cartList.Any(c => c.Id == x.Id)).AsQueryable();

            if (!string.IsNullOrEmpty(request.Filter.Value))
            {
                var term = request.Filter.Value.ToLower();
                query = query.Where(x => x.ItemName.ToLower().Contains(term) || x.ItemBarcode.ToLower().Contains(term));
            }
            else
            {
                query = query.Take(50);
            }
            return await Task.FromResult(request.ApplyTo(query));
        }

        private void OnAutoCompleteChanged(ItemModels selectedItem)
        {
            tempSelectedItem = selectedItem;

            if (selectedItem != null)
            {
                tempSelectedItemId = selectedItem.Id;
                tempSelectedBarcode = selectedItem.ItemBarcode;
            }
            else
            {
                tempSelectedItemId = 0;
                tempSelectedBarcode = "";
            }
            StateHasChanged();
        }

        // --- Database Loaders ---
        private void LoadDepartments()
        {
            try
            {
                departmentList.Clear();
                using (var conn = new Myconnection().GetConnection())
                {
                    if (conn.State != ConnectionState.Open) conn.Open();
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
                    if (conn.State != ConnectionState.Open) conn.Open();
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
                    if (conn.State != ConnectionState.Open) conn.Open();
                    using (var cmd = new NpgsqlCommand(@"SELECT ""Id"", ""BorrowerName"", ""Department"", ""DepartmentCode"", ""Equipment"", ""Barcode"", ""BorrowDate"", ""DueDate"", ""ReturnDate"", ""Status"", ""PhotoPath"", ""ItemId"" FROM public.borrow_tb ORDER BY ""Id"" DESC", conn))
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
                            if (reader["Status"] != DBNull.Value) b.Status = (BorrowStatus)Convert.ToInt16(reader["Status"]);
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
            if (int.TryParse(e.Value?.ToString(), out int result)) filterStatus = result;
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