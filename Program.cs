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
            Console.WriteLine("=== Database Schema Comparer (A -> B) ===");

            // --- CENTRALIZED CONFIGURATION ---
            // Update these variables for your environment
            string hostA_IP = "127.0.0.1"; // Host A IP or Hostname
            string dbA_Name = "SourceDB";   // Host A Database Name

            string hostB_IP = "192.168.1.100"; // Host B IP or Hostname
            string dbB_Name = "TargetDB";      // Host B Database Name

            // Safety: The generated SQL script will ONLY be allowed to run on this IP.
            string targetServerIP = "192.168.1.100";

            // Extra Dependency Check (Optional)
            string dependencyCheckName = "sp_DrgSub_UpdateTbl";
            // ---------------------------------

            // Constructing Connection Strings with Windows Authentication
            // Integrated Security=True; enables Windows Auth
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

                // Initialize Full Sync Script
                var fullSyncScript = new System.Text.StringBuilder();
                fullSyncScript.AppendLine("-- Full Synchronization Script: Host A -> Host B");
                fullSyncScript.AppendLine($"-- Generated at: {DateTime.Now}");

                // --- SAFETY CHECK BLOCK ---
                fullSyncScript.AppendLine($"-- SAFETY CHECK: Only allow execution on {targetServerIP}");
                fullSyncScript.AppendLine($"DECLARE @TargetIP VARCHAR(50) = '{targetServerIP}';");
                fullSyncScript.AppendLine("DECLARE @ActualIP VARCHAR(50) = CAST(CONNECTIONPROPERTY('local_net_address') AS VARCHAR(50));");
                fullSyncScript.AppendLine("IF (@ActualIP <> @TargetIP OR @ActualIP IS NULL)");
                fullSyncScript.AppendLine("BEGIN");
                fullSyncScript.AppendLine($"    DECLARE @ErrMsg NVARCHAR(200) = N'SAFETY ABORT: This script is restricted to {targetServerIP}. Current Server IP: ' + ISNULL(@ActualIP, 'Local/Unknown');");
                fullSyncScript.AppendLine("    RAISERROR(@ErrMsg, 20, 1) WITH LOG; -- Severity 20 will terminate the connection");
                fullSyncScript.AppendLine("    SET NOEXEC ON; -- Stop further execution in this session");
                fullSyncScript.AppendLine("END");
                fullSyncScript.AppendLine("GO\n");
                // --------------------------

                fullSyncScript.AppendLine($"USE [{dbB_Name}]; -- Automatically set to your target DB");
                fullSyncScript.AppendLine("GO\n");

                // 1. Table Sync
                CompareAndSyncTables(schemaA, schemaB, fullSyncScript);

                // 2. Stored Procedure Sync
                Console.WriteLine("\nReading Stored Procedures from Host A...");
                var procsA = GetStoredProcedures(connStrA);
                Console.WriteLine($"Loaded {procsA.Count} procedures from Host A.");

                Console.WriteLine("Reading Stored Procedures from Host B...");
                var procsB = GetStoredProcedures(connStrB);
                Console.WriteLine($"Loaded {procsB.Count} procedures from Host B.");

                CompareAndSyncStoredProcedures(procsA, procsB, fullSyncScript, connStrA, dependencyCheckName);

                // Write File
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
            script.AppendLine("-- SECTION 1: TABLE SYNCHRONIZATION");
            script.AppendLine("-- =============================================");

            var sourceTables = source.GroupBy(c => new { c.TableSchema, c.TableName });
            bool foundIssues = false;

            foreach (var tableGroup in sourceTables)
            {
                string schema = tableGroup.Key.TableSchema;
                string table = tableGroup.Key.TableName;
                string fullTableName = $"[{schema}].[{table}]";

                // Check if table exists in target
                var targetTableCols = target.Where(c => c.TableSchema == schema && c.TableName == table).ToList();

                if (!targetTableCols.Any())
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Missing Table] {fullTableName} exists in A but NOT in B. (Generating CREATE TABLE)");
                    Console.ResetColor();

                    script.AppendLine($"-- [Missing Table] Creating {fullTableName}");
                    script.AppendLine($"CREATE TABLE {fullTableName} (");

                    var cols = tableGroup.OrderBy(c => c.OrdinalPosition).ToList(); // Assuming OrdinalPosition is captured implicitly by list order or we need to add it. 
                                                                                    // Note: Previous GetSchema query ORDER BY handles order, but we don't store OrdinalPosition in ColumnInfo.
                                                                                    // We will just iterate in order they came (which is ordered).

                    for (int i = 0; i < cols.Count; i++)
                    {
                        var col = cols[i];
                        string typeDef = GetColumnTypeString(col);
                        string nullable = col.IsNullable == "YES" ? "NULL" : "NOT NULL";
                        string comma = (i < cols.Count - 1) ? "," : "";
                        script.AppendLine($"    [{col.ColumnName}] {typeDef} {nullable}{comma}");
                    }
                    script.AppendLine(");");
                    script.AppendLine("-- WARNING: Primary Keys, Indexes, and Defaults are NOT included in this auto-generated script.");
                    script.AppendLine("GO");

                    foundIssues = true;
                    continue;
                }

                // Check columns
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

                        script.AppendLine($"-- [Missing Column] Adding {colA.ColumnName} to {fullTableName}");
                        script.AppendLine($"ALTER TABLE {fullTableName} ADD [{colA.ColumnName}] {typeDef} {nullable};");
                        script.AppendLine("GO");

                        foundIssues = true;
                    }
                    else
                    {
                        // Compare properties
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

                            script.AppendLine($"-- [Mismatch] Updating {colA.ColumnName} in {fullTableName} Reason: {diffMsg}");
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
            // For decimal/numeric, we ideally need precision/scale. 
            // Since we don't have it in ColumnInfo yet, we return base type. 
            // This is a known limitation of this simple tool.
            return type;
        }

        static List<ProcedureInfo> GetStoredProcedures(string connectionString)
        {
            var procs = new List<ProcedureInfo>();

            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                // Using sys.sql_modules because INFORMATION_SCHEMA.ROUTINES often truncates definition at 4000 chars
                string query = @"
                    SELECT 
                        s.name AS SchemaName,
                        o.name AS ProcedureName,
                        m.definition AS Definition
                    FROM sys.sql_modules m
                    INNER JOIN sys.objects o ON m.object_id = o.object_id
                    INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                    WHERE o.type = 'P'"; // 'P' stands for SQL Stored Procedure

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
            script.AppendLine("-- SECTION 2: STORED PROCEDURE SYNCHRONIZATION");
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
                    Console.WriteLine($"[Missing SP] {fullProcName} exists in A but NOT in B.");
                    Console.ResetColor();
                    needsSync = true;
                    reason = "Missing in Target";
                    missingCount++;
                }
                else
                {
                    string defA = NormalizeSql(procA.Definition);
                    string defB = NormalizeSql(procB.Definition);

                    if (defA != defB)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[Content Mismatch] {fullProcName} content is different.");
                        Console.ResetColor();
                        needsSync = true;
                        reason = "Content Mismatch";
                        mismatchCount++;

                        // --- Debug: Write files for manual diff check ---
                        string safeName = procA.ProcedureName.Replace("\\", "_").Replace("/", "_");
                        System.IO.File.WriteAllText(System.IO.Path.Combine("Diff_Check", $"{safeName}_A.sql"), procA.Definition);
                        System.IO.File.WriteAllText(System.IO.Path.Combine("Diff_Check", $"{safeName}_B.sql"), procB.Definition);
                    }
                }

                if (needsSync)
                {
                    script.AppendLine($"-- Syncing {fullProcName} ({reason})");

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

            // --- Extra Check for the specific missing dependency user mentioned ---
            CheckSpecificDependency(connStrA, dependencyCheckName);
        }

        static void CheckSpecificDependency(string connectionString, string objectName)
        {
            Console.WriteLine($"\n--- Deep Check for dependency '{objectName}' in Host A ---");
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
                        Console.WriteLine($"Found '{objectName}' in Host A! It is a: {result}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"'{objectName}' does NOT exist in Host A either.");
                        Console.WriteLine("This means the source code in A is also referencing a missing object.");
                        Console.ResetColor();
                    }
                }
            }
        }

        static string ConvertToCreateOrAlter(string definition)
        {
            // Regex to find "CREATE PROCEDURE" or "CREATE PROC", ignoring case
            // We use [\s\S]*? to match any preceding comments/whitespace non-greedily if we wanted to preserve them,
            // but here we just want to replace the keyword.
            var regex = new System.Text.RegularExpressions.Regex(@"\bCREATE\s+(PROC|PROCEDURE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Replace only the first occurrence
            return regex.Replace(definition, "CREATE OR ALTER PROCEDURE", 1);
        }

        static string NormalizeSql(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return "";

            string s = sql;

            // 1. Standardize "CREATE OR ALTER" to "CREATE"
            s = System.Text.RegularExpressions.Regex.Replace(s,
                @"\bCREATE\s+OR\s+ALTER\s+(PROC|PROCEDURE)\b",
                "CREATE PROCEDURE",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 2. Standardize "CREATE PROC" to "CREATE PROCEDURE"
            s = System.Text.RegularExpressions.Regex.Replace(s,
                @"\bCREATE\s+PROC\s+",
                "CREATE PROCEDURE ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // 3. REMOVE ALL COMMENTS (Optional but safer for exact logic match)
            // This handles -- comments
            s = System.Text.RegularExpressions.Regex.Replace(s, @"--.*", "");
            // This handles /* */ comments
            s = System.Text.RegularExpressions.Regex.Replace(s, @"/\*[\s\S]*?\*/", "");

            // 4. Collapse all whitespace (including newlines/tabs) into single spaces
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");

            return s.Trim().ToLower(); // Compare in lower case for total safety
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
