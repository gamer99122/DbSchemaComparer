using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace DbSchemaComparer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== 資料庫結構比對工具 (A -> B) ===");

            // --- 集中式配置 ---
            // 請根據您的環境更新這些變數
            string hostA_IP = "127.0.0.1"; // 主機 A 的 IP 或主機名稱
            string dbA_Name = "SourceDB";   // 主機 A 的資料庫名稱

            string hostB_IP = "192.168.1.100"; // 主機 B 的 IP 或主機名稱
            string dbB_Name = "TargetDB";      // 主機 B 的資料庫名稱

            // 安全性：產生的 SQL 腳本將只允許在此 IP 上執行
            string targetServerIP = "192.168.1.100";

            // 額外的相依性檢查 (可選)
            string dependencyCheckName = "sp_DrgSub_UpdateTbl";
            // ---------------------------------

            // 使用 Windows 驗證建立連線字串
            // Integrated Security=True; 啟用 Windows 驗證
            string connStrA = $"Server={hostA_IP};Database={dbA_Name};Integrated Security=True;TrustServerCertificate=True;";
            string connStrB = $"Server={hostB_IP};Database={dbB_Name};Integrated Security=True;TrustServerCertificate=True;";

            Console.WriteLine($"Comparing:");
            Console.WriteLine($"  Source (A): {hostA_IP} [{dbA_Name}]");
            Console.WriteLine($"  Target (B): {hostB_IP} [{dbB_Name}]");
            Console.WriteLine($"  Target Server IP for Script Safety: {targetServerIP}");
            Console.WriteLine("-------------------------------------------");

            try
            {
                Console.WriteLine("Reading schema from Host A...");
                var schemaA = GetSchema(connStrA);
                Console.WriteLine($"Loaded {schemaA.Count} columns from Host A.");

                Console.WriteLine("Reading schema from Host B...");
                var schemaB = GetSchema(connStrB);
                Console.WriteLine($"Loaded {schemaB.Count} columns from Host B.");

                // 初始化完整同步腳本
                var fullSyncScript = new System.Text.StringBuilder();
                fullSyncScript.AppendLine("-- 完整同步腳本：主機 A -> 主機 B");
                fullSyncScript.AppendLine($"-- 產生時間：{DateTime.Now}");

                // --- 安全性檢查區塊 ---
                fullSyncScript.AppendLine($"-- 安全性檢查：僅允許在 {targetServerIP} 上執行");
                fullSyncScript.AppendLine($"DECLARE @TargetIP VARCHAR(50) = '{targetServerIP}';");
                fullSyncScript.AppendLine("DECLARE @ActualIP VARCHAR(50) = CAST(CONNECTIONPROPERTY('local_net_address') AS VARCHAR(50));");
                fullSyncScript.AppendLine("IF (@ActualIP <> @TargetIP OR @ActualIP IS NULL)");
                fullSyncScript.AppendLine("BEGIN");
                fullSyncScript.AppendLine($"    DECLARE @ErrMsg NVARCHAR(200) = N'安全性中止：此腳本僅限於 {targetServerIP} 執行。目前伺服器 IP：' + ISNULL(@ActualIP, 'Local/Unknown');");
                fullSyncScript.AppendLine("    RAISERROR(@ErrMsg, 20, 1) WITH LOG; -- 嚴重性等級 20 將終止連線");
                fullSyncScript.AppendLine("    SET NOEXEC ON; -- 停止在此工作階段中進一步執行");
                fullSyncScript.AppendLine("END");
                fullSyncScript.AppendLine("GO\n");
                // --------------------------

                fullSyncScript.AppendLine($"USE [{dbB_Name}]; -- 自動設定為您的目標資料庫");
                fullSyncScript.AppendLine("GO\n");

                // 1. 資料表同步
                CompareAndSyncTables(schemaA, schemaB, fullSyncScript);

                // 2. 預存程序同步
                Console.WriteLine("\nReading Stored Procedures from Host A...");
                var procsA = GetStoredProcedures(connStrA);
                Console.WriteLine($"Loaded {procsA.Count} procedures from Host A.");

                Console.WriteLine("Reading Stored Procedures from Host B...");
                var procsB = GetStoredProcedures(connStrB);
                Console.WriteLine($"Loaded {procsB.Count} procedures from Host B.");

                CompareAndSyncStoredProcedures(procsA, procsB, fullSyncScript, connStrA, dependencyCheckName);

                // 寫入檔案
                string fileName = "Sync_Full_Schema_A_to_B.sql";
                System.IO.File.WriteAllText(fileName, fullSyncScript.ToString());

                Console.WriteLine("\n==================================================");
                Console.WriteLine($"FULL SYNC SCRIPT GENERATED: {System.IO.Path.GetFullPath(fileName)}");
                Console.WriteLine("Please review the script carefully before running it on Host B!");
                Console.WriteLine("==================================================");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"An error occurred: {ex.Message}");
                Console.ResetColor();
            }
        }

        static List<ColumnInfo> GetSchema(string connectionString)
        {
            var columns = new List<ColumnInfo>();

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string query = @"
                    SELECT 
                        TABLE_SCHEMA, 
                        TABLE_NAME, 
                        COLUMN_NAME, 
                        DATA_TYPE, 
                        CHARACTER_MAXIMUM_LENGTH, 
                        IS_NULLABLE,
                        ORDINAL_POSITION 
                    FROM INFORMATION_SCHEMA.COLUMNS
                    ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";

                using (var cmd = new SqlCommand(query, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        columns.Add(new ColumnInfo
                        {
                            TableSchema = reader["TABLE_SCHEMA"].ToString(),
                            TableName = reader["TABLE_NAME"].ToString(),
                            ColumnName = reader["COLUMN_NAME"].ToString(),
                            DataType = reader["DATA_TYPE"].ToString(),
                            MaxLength = reader["CHARACTER_MAXIMUM_LENGTH"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["CHARACTER_MAXIMUM_LENGTH"]),
                            IsNullable = reader["IS_NULLABLE"].ToString(),
                            OrdinalPosition = Convert.ToInt32(reader["ORDINAL_POSITION"])
                        });
                    }
                }
            }
            return columns;
        }

        static void CompareAndSyncTables(List<ColumnInfo> source, List<ColumnInfo> target, System.Text.StringBuilder script)
        {
            Console.WriteLine("\n--- Starting Table Schema Comparison & Script Generation ---\n");

            script.AppendLine("-- =============================================");
            script.AppendLine("-- 區段 1：資料表同步");
            script.AppendLine("-- =============================================");

            var sourceTables = source.GroupBy(c => new { c.TableSchema, c.TableName });
            bool foundIssues = false;

            foreach (var tableGroup in sourceTables)
            {
                string schema = tableGroup.Key.TableSchema;
                string table = tableGroup.Key.TableName;
                string fullTableName = $"[{schema}].[{table}]";

                // 檢查資料表是否存在於目標中
                var targetTableCols = target.Where(c => c.TableSchema == schema && c.TableName == table).ToList();

                if (!targetTableCols.Any())
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Missing Table] {fullTableName} exists in A but NOT in B. (Generating CREATE TABLE)");
                    Console.ResetColor();

                    script.AppendLine($"-- [缺少資料表] 建立 {fullTableName}");
                    script.AppendLine($"CREATE TABLE {fullTableName} (");

                    var cols = tableGroup.OrderBy(c => c.OrdinalPosition).ToList(); // 假設 OrdinalPosition 由列表順序隱含捕獲，或者我們需要新增它
                                                                                    // 注意：先前的 GetSchema 查詢 ORDER BY 處理順序，但我們沒有在 ColumnInfo 中儲存 OrdinalPosition
                                                                                    // 我們將按照它們來的順序進行迭代（已排序）

                    for (int i = 0; i < cols.Count; i++)
                    {
                        var col = cols[i];
                        string typeDef = GetColumnTypeString(col);
                        string nullable = col.IsNullable == "YES" ? "NULL" : "NOT NULL";
                        string comma = (i < cols.Count - 1) ? "," : "";
                        script.AppendLine($"    [{col.ColumnName}] {typeDef} {nullable}{comma}");
                    }
                    script.AppendLine(");");
                    script.AppendLine("-- 警告：此自動產生的腳本不包含主鍵、索引和預設值。");
                    script.AppendLine("GO");

                    foundIssues = true;
                    continue;
                }

                // 檢查欄位
                foreach (var colA in tableGroup)
                {
                    var colB = targetTableCols.FirstOrDefault(c => c.ColumnName == colA.ColumnName);

                    if (colB == null)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[Missing Column] {fullTableName}.[{colA.ColumnName}] exists in A but NOT in B. (Generating ADD COLUMN)");
                        Console.ResetColor();

                        string typeDef = GetColumnTypeString(colA);
                        string nullable = colA.IsNullable == "YES" ? "NULL" : "NOT NULL";

                        script.AppendLine($"-- [缺少欄位] 將 {colA.ColumnName} 加入 {fullTableName}");
                        script.AppendLine($"ALTER TABLE {fullTableName} ADD [{colA.ColumnName}] {typeDef} {nullable};");
                        script.AppendLine("GO");

                        foundIssues = true;
                    }
                    else
                    {
                        // 比較屬性
                        bool diff = false;
                        string diffMsg = "";

                        if (!string.Equals(colA.DataType, colB.DataType, StringComparison.OrdinalIgnoreCase))
                        {
                            diff = true;
                            diffMsg += $" Type({colA.DataType} vs {colB.DataType})";
                        }

                        if (colA.MaxLength != colB.MaxLength)
                        {
                            diff = true;
                            diffMsg += $" MaxLength({(colA.MaxLength.HasValue ? colA.MaxLength.ToString() : "NULL")} vs {(colB.MaxLength.HasValue ? colB.MaxLength.ToString() : "NULL")})";
                        }

                        if (!string.Equals(colA.IsNullable, colB.IsNullable, StringComparison.OrdinalIgnoreCase))
                        {
                            diff = true;
                            diffMsg += $" Nullable({colA.IsNullable} vs {colB.IsNullable})";
                        }

                        if (diff)
                        {
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"[Mismatch]       {fullTableName}.[{colA.ColumnName}] ->{diffMsg} (Generating ALTER COLUMN)");
                            Console.ResetColor();

                            string typeDef = GetColumnTypeString(colA);
                            string nullable = colA.IsNullable == "YES" ? "NULL" : "NOT NULL";

                            script.AppendLine($"-- [不符] 更新 {fullTableName} 中的 {colA.ColumnName}，原因：{diffMsg}");
                            script.AppendLine($"ALTER TABLE {fullTableName} ALTER COLUMN [{colA.ColumnName}] {typeDef} {nullable};");
                            script.AppendLine("GO");

                            foundIssues = true;
                        }
                    }
                }
            }

            if (!foundIssues)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("No Table Schema discrepancies found! Host B matches Host A.");
                Console.ResetColor();
            }
        }

        static string GetColumnTypeString(ColumnInfo col)
        {
            string type = col.DataType;
            if (type == "varchar" || type == "nvarchar" || type == "char" || type == "nchar" || type == "binary" || type == "varbinary")
            {
                string len = (col.MaxLength == -1 || col.MaxLength == null) ? "MAX" : col.MaxLength.ToString();
                return $"{type}({len})";
            }
            // 對於 decimal/numeric，理想情況下我們需要精確度/小數位數
            // 由於我們在 ColumnInfo 中還沒有它，我們返回基本型別
            // 這是此簡單工具的已知限制
            return type;
        }

        static List<ProcedureInfo> GetStoredProcedures(string connectionString)
        {
            var procs = new List<ProcedureInfo>();

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                // 使用 sys.sql_modules 因為 INFORMATION_SCHEMA.ROUTINES 經常在 4000 個字元處截斷定義
                string query = @"
                    SELECT 
                        s.name AS SchemaName,
                        o.name AS ProcedureName,
                        m.definition AS Definition
                    FROM sys.sql_modules m
                    INNER JOIN sys.objects o ON m.object_id = o.object_id
                    INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                    WHERE o.type = 'P'"; // 'P' 代表 SQL 預存程序

                using (var cmd = new SqlCommand(query, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        procs.Add(new ProcedureInfo
                        {
                            SchemaName = reader["SchemaName"].ToString(),
                            ProcedureName = reader["ProcedureName"].ToString(),
                            Definition = reader["Definition"].ToString()
                        });
                    }
                }
            }
            return procs;
        }

        static void CompareAndSyncStoredProcedures(List<ProcedureInfo> source, List<ProcedureInfo> target, System.Text.StringBuilder script, string connStrA, string dependencyCheckName)
        {
            Console.WriteLine("\n--- Starting Stored Procedure Comparison & Script Generation ---\n");

            script.AppendLine("\n-- =============================================");
            script.AppendLine("-- 區段 2：預存程序同步");
            script.AppendLine("-- =============================================");

            int missingCount = 0;
            int mismatchCount = 0;

            foreach (var procA in source)
            {
                string fullProcName = $"[{procA.SchemaName}].[{procA.ProcedureName}]";
                var procB = target.FirstOrDefault(p => p.SchemaName == procA.SchemaName && p.ProcedureName == procA.ProcedureName);

                bool needsSync = false;
                string reason = "";

                if (procB == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[缺少 SP] {fullProcName} 存在於 A 但不存在於 B。");
                    Console.ResetColor();
                    needsSync = true;
                    reason = "目標中缺少";
                    missingCount++;
                }
                else
                {
                    string defA = NormalizeSql(procA.Definition);
                    string defB = NormalizeSql(procB.Definition);

                    if (defA != defB)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[內容不符] {fullProcName} 內容不同。");
                        Console.ResetColor();
                        needsSync = true;
                        reason = "內容不符";
                        mismatchCount++;

                        // --- 除錯：寫入檔案以進行手動差異檢查 ---
                        string safeName = procA.ProcedureName.Replace("\\", "_").Replace("/", "_");
                        System.IO.File.WriteAllText(System.IO.Path.Combine("Diff_Check", $"{safeName}_A.sql"), procA.Definition);
                        System.IO.File.WriteAllText(System.IO.Path.Combine("Diff_Check", $"{safeName}_B.sql"), procB.Definition);
                    }
                }

                if (needsSync)
                {
                    script.AppendLine($"-- 同步 {fullProcName} ({reason})");

                    string newDefinition = ConvertToCreateOrAlter(procA.Definition);

                    script.AppendLine(newDefinition);
                    script.AppendLine("GO");
                    script.AppendLine("--------------------------------------------------");
                }
            }

            if (missingCount == 0 && mismatchCount == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("No Stored Procedure discrepancies found! Everything is in sync.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"Found {missingCount} missing and {mismatchCount} mismatched procedures.");
                Console.WriteLine("Details and sync SQL have been generated.");
                Console.ResetColor();
            }

            // --- 使用者提到的特定缺少相依性的額外檢查 ---
            CheckSpecificDependency(connStrA, dependencyCheckName);
        }

        static void CheckSpecificDependency(string connectionString, string objectName)
        {
            Console.WriteLine($"\n--- 在主機 A 中深度檢查相依性 '{objectName}' ---");
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string query = "SELECT type_desc FROM sys.objects WHERE name = @name";
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@name", objectName);
                    var result = cmd.ExecuteScalar();
                    if (result != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"在主機 A 中找到 '{objectName}'！它是一個：{result}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"'{objectName}' 在主機 A 中也不存在。");
                        Console.WriteLine("這意味著 A 中的原始碼也在參考一個缺少的物件。");
                        Console.ResetColor();
                    }
                }
            }
        }

        static string ConvertToCreateOrAlter(string definition)
        {
            // 正規表示式來找到 "CREATE PROCEDURE" 或 "CREATE PROC"，忽略大小寫
            // 我們使用 [\s\S]*? 來非貪婪地匹配任何前面的註解/空白，如果我們想保留它們
            // 但這裡我們只想替換關鍵字
            var regex = new System.Text.RegularExpressions.Regex(@"\bCREATE\s+(PROC|PROCEDURE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 僅替換第一次出現
            return regex.Replace(definition, "CREATE OR ALTER PROCEDURE", 1);
        }

        static string NormalizeSql(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return "";

            string s = sql;

            // 1. 標準化 "CREATE OR ALTER" 為 "CREATE"
            s = System.Text.RegularExpressions.Regex.Replace(s,
                @"\bCREATE\s+OR\s+ALTER\s+(PROC|PROCEDURE)\b",
                "CREATE PROCEDURE",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 2. 標準化 "CREATE PROC" 為 "CREATE PROCEDURE"
            s = System.Text.RegularExpressions.Regex.Replace(s,
                @"\bCREATE\s+PROC\s+",
                "CREATE PROCEDURE ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 3. 移除所有註解 (可選但對於精確邏輯匹配更安全)
            // 這處理 -- 註解
            s = System.Text.RegularExpressions.Regex.Replace(s, @"--.*", "");
            // 這處理 /* */ 註解
            s = System.Text.RegularExpressions.Regex.Replace(s, @"/\*[\s\S]*?\*/", "");

            // 4. 將所有空白 (包括換行/tab) 折疊成單一空格
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");

            return s.Trim().ToLower(); // 以小寫比較以確保完全安全
        }
    }

    class ProcedureInfo
    {
        public string SchemaName { get; set; }
        public string ProcedureName { get; set; }
        public string Definition { get; set; }
    }

    class ColumnInfo
    {
        public string TableSchema { get; set; }
        public string TableName { get; set; }
        public string ColumnName { get; set; }
        public string DataType { get; set; }
        public int? MaxLength { get; set; }
        public string IsNullable { get; set; }
        public int OrdinalPosition { get; set; }
    }
}
