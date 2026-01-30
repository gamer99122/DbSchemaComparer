# Database Schema Comparer & Synchronizer (MSSQL)

這是一個基於 C# .NET 8 開發的主控台應用程式，專門用於比對並同步兩個 Microsoft SQL Server (MSSQL) 資料庫的結構。

主要用途是將 **主機 A (來源/Source)** 的資料表結構與預存程序，同步到 **主機 B (目標/Target)**。

## 🚀 主要功能

### 1. 資料表比對 (Table Comparison)
工具會檢查以下差異，並生成對應的 SQL 指令：
*   **缺少資料表**：生成 `CREATE TABLE` (包含完整欄位定義與型別)。
*   **缺少欄位**：生成 `ALTER TABLE ... ADD ...`。
*   **欄位屬性不一致**：檢查型別 (Type)、長度 (Length)、Nullable 屬性，生成 `ALTER TABLE ... ALTER COLUMN ...`。

### 2. 預存程序比對 (Stored Procedure Comparison)
具備 **智慧比對 (Smart Diff)** 邏輯，能過濾格式差異，只針對真正的邏輯變更進行同步：
*   **忽略格式差異**：自動忽略空白鍵、換行符號、縮排的差異。
*   **忽略註解**：比對前會移除 `--` 與 `/* */` 註解，避免因註解不同而誤判。
*   **忽略語法關鍵字**：自動識別並統一 `CREATE` 與 `CREATE OR ALTER`。
*   **同步方式**：生成 `CREATE OR ALTER PROCEDURE` 指令，確保更新時**不會遺失原本的權限設定**。

### 3. 差異檢視 (Diff Check)
*   當發現預存程序內容不一致時，會自動將 A 與 B 的版本分別匯出至 `Diff_Check/` 資料夾 (如 `sp_Name_A.sql` vs `sp_Name_B.sql`)，方便使用比對工具 (如 VS Code, WinMerge) 進行人工確認。

### 4. 安全防護 (Safety Mechanisms)
*   **唯讀分析**：C# 程式本身對資料庫僅執行 `SELECT` (唯讀)，不會直接修改資料庫。
*   **IP 鎖定執行**：產生的同步 SQL 腳本內建 **IP 安全檢查**。
    *   預設限制只能在 `172.17.2.19` (測試環境) 執行。
    *   若在錯誤的主機執行，腳本會自動報錯並終止 (`SET NOEXEC ON`)。

---

## 🛠️ 環境需求

*   **Runtime**: .NET 8.0 SDK 或 Runtime。
*   **Authentication**: 使用 **Windows 驗證 (Windows Authentication)**。請確保執行此程式的電腦與使用者帳號具有存取兩台 SQL Server 的權限。

---

## ⚙️ 設定方式

目前的設定位於 `Program.cs` 的 `Main` 方法頂部，請依據需求修改：

```csharp
// --- CENTRALIZED CONFIGURATION ---
string hostA_IP = "127.0.0.1";      // 主機 A (來源)
string dbA_Name = "SourceDB";       // 主機 A 資料庫

string hostB_IP = "192.168.1.100";  // 主機 B (目標)
string dbB_Name = "TargetDB";       // 主機 B 資料庫
```

若要修改 **SQL 腳本允許執行的 IP**，請修改 `CompareAndSyncTables` 方法中的這行：
```csharp
fullSyncScript.AppendLine("DECLARE @TargetIP VARCHAR(50) = '172.17.2.19';"); 
```

---

## 📦 使用步驟

1.  **進入專案目錄**
    ```bash
    cd DbSchemaComparer
    ```

2.  **執行程式**
    ```bash
    dotnet run
    ```

3.  **查看結果**
    程式執行完畢後，會顯示比對摘要：
    *   **Sync_Full_Schema_A_to_B.sql**：完整的同步腳本。
    *   **Diff_Check/**：若有內容不一致的預存程序，可在此資料夾查看詳細差異。

4.  **執行同步**
    *   使用 SSMS (SQL Server Management Studio) 開啟 `Sync_Full_Schema_A_to_B.sql`。
    *   連線至 **主機 B (目標)**。
    *   執行腳本。

---

## 📝 注意事項

*   **資料表同步限制**：自動生成的 `CREATE TABLE` 目前僅包含欄位定義，**暫不包含** Primary Key (PK)、索引 (Index) 與預設值 (Default Constraint)。若為全新資料表，建議同步後手動補上索引。
*   **相依性檢查**：程式結束時會順便檢查 A 主機是否存在 `sp_DrgSub_UpdateTbl` (或其他指定物件)，以協助診斷相依性錯誤。
