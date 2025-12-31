using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbToEntity.Core.Models;
using Npgsql;

namespace DbToEntity.Core
{
    public class PostgresMetadataProvider : IPostgresMetadataProvider
    {
        public async Task<List<TableMetadata>> GetTablesAsync(string connectionString, string schema, IEnumerable<string>? tableNames = null)
        {
            var tables = new List<TableMetadata>();

            using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            // 1. Get Tables (Parent only or specific tables)
            // We intentionally filter for 'r' (ordinary table) or 'p' (partitioned table)
            // BUT we must exclude child partitions. 
            // In PG, child partitions have pg_inherits entries.
            var tableQuery = @"
                SELECT c.oid, c.relname, c.relkind
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE (@schema IS NULL OR n.nspname = @schema)
                AND (@schema IS NOT NULL OR (n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp%'))
                AND c.relkind IN ('r', 'p')
                AND NOT EXISTS (SELECT 1 FROM pg_inherits i WHERE i.inhrelid = c.oid) -- Exclude child partitions
            ";

            if (tableNames != null && tableNames.Any())
            {
                // Simplified filtering for demo purposes; in production use ANY(@names)
                var names = string.Join("','", tableNames);
                tableQuery += $" AND c.relname IN ('{names}')";
            }

            using (var cmd = new NpgsqlCommand(tableQuery, conn))
            {
                cmd.Parameters.AddWithValue("schema", schema);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        tables.Add(new TableMetadata
                        {
                            Schema = schema,
                            Name = reader.GetString(1),
                            IsPartitioned = reader.GetChar(2) == 'p'
                        });
                    }
                }
            }

            // 2. Get Columns for each table
            // For efficiency, we could fetch all at once, but loop is easier for MVP clarity
            foreach (var table in tables)
            {
                await LoadColumnsAsync(conn, table);
                await LoadPrimaryKeysAsync(conn, table);
                await LoadForeignKeysAsync(conn, table);
            }

            // 3. Post-processing: Filter Foreign Keys and Populate Inverse
            var validTableNames = new HashSet<string>(tables.Select(t => t.Name));
            foreach (var table in tables)
            {
                // Remove FKs pointing to non-existent tables (e.g., partitions excluded from extraction)
                table.ForeignKeys.RemoveAll(fk => !validTableNames.Contains(fk.TargetTable));

                // Populate inverse relationships
                foreach (var fk in table.ForeignKeys)
                {
                    var targetTable = tables.FirstOrDefault(t => t.Name == fk.TargetTable);
                    if (targetTable != null)
                    {
                        targetTable.ReferencingForeignKeys.Add(fk);
                    }
                }
            }

            return tables;
        }

        private async Task LoadColumnsAsync(NpgsqlConnection conn, TableMetadata table)
        {
            var query = @"
                SELECT column_name, udt_name, is_nullable, character_maximum_length, column_default
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name = @table
                ORDER BY ordinal_position
            ";

            using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("schema", table.Schema);
            cmd.Parameters.AddWithValue("table", table.Name);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                table.Columns.Add(new ColumnMetadata
                {
                    Name = reader.GetString(0),
                    DataType = reader.GetString(1),
                    IsNullable = reader.GetString(2) == "YES",
                    MaxLength = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    DefaultValue = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }
        }

        private async Task LoadPrimaryKeysAsync(NpgsqlConnection conn, TableMetadata table)
        {
            var query = @"
                SELECT kcu.column_name, tc.constraint_name
                FROM information_schema.key_column_usage kcu
                JOIN information_schema.table_constraints tc
                  ON kcu.constraint_name = tc.constraint_name
                  AND kcu.table_schema = tc.table_schema
                WHERE tc.constraint_type = 'PRIMARY KEY'
                  AND tc.table_schema = @schema
                  AND tc.table_name = @table
                ORDER BY kcu.ordinal_position
            ";

            using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("schema", table.Schema);
            cmd.Parameters.AddWithValue("table", table.Name);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                table.PrimaryKeys.Add(reader.GetString(0));
                if (table.PrimaryKeyName == null)
                    table.PrimaryKeyName = reader.GetString(1);
            }
        }

        private async Task LoadForeignKeysAsync(NpgsqlConnection conn, TableMetadata table)
        {
            var query = @"
                SELECT
                    con.conname,
                    c.relname AS source_table,
                    (SELECT array_to_string(array_agg(a.attname ORDER BY array_position(con.conkey, a.attnum)), ',')
                     FROM pg_attribute a
                     WHERE a.attrelid = con.conrelid AND a.attnum = ANY(con.conkey)
                    ) AS source_columns,
                    tn.nspname AS target_schema,
                    t.relname AS target_table,
                    (SELECT array_to_string(array_agg(a.attname ORDER BY array_position(con.confkey, a.attnum)), ',')
                     FROM pg_attribute a
                     WHERE a.attrelid = con.confrelid AND a.attnum = ANY(con.confkey)
                    ) AS target_columns
                FROM pg_constraint con
                JOIN pg_class c ON c.oid = con.conrelid
                JOIN pg_namespace n ON n.oid = con.connamespace
                JOIN pg_class t ON t.oid = con.confrelid
                JOIN pg_namespace tn ON tn.oid = t.relnamespace
                WHERE con.contype = 'f'
                  AND (@schema IS NULL OR n.nspname = @schema)
                  AND c.relname = @table
            ";

            using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("schema", table.Schema);
            cmd.Parameters.AddWithValue("table", table.Name);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                table.ForeignKeys.Add(new ForeignKeyMetadata
                {
                    ConstraintName = reader.GetString(0),
                    SourceTable = reader.GetString(1),
                    SourceColumns = reader.GetString(2).Split(',').ToList(),
                    TargetSchema = reader.GetString(3),
                    TargetTable = reader.GetString(4),
                    TargetColumns = reader.GetString(5).Split(',').ToList()
                });
            }
        }
    }
}
