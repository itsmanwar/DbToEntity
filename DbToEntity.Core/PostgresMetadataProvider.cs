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
                    kcu.constraint_name,
                    kcu.column_name,
                    ccu.table_schema AS target_schema,
                    ccu.table_name AS target_table,
                    ccu.column_name AS target_column
                FROM information_schema.key_column_usage kcu
                JOIN information_schema.table_constraints tc
                  ON kcu.constraint_name = tc.constraint_name
                  AND kcu.table_schema = tc.table_schema
                JOIN information_schema.constraint_column_usage ccu
                  ON ccu.constraint_name = tc.constraint_name
                  AND ccu.table_schema = tc.table_schema
                WHERE tc.constraint_type = 'FOREIGN KEY'
                  AND tc.table_schema = @schema
                  AND tc.table_name = @table
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
                    SourceColumn = reader.GetString(1),
                    TargetSchema = reader.GetString(2),
                    TargetTable = reader.GetString(3),
                    TargetColumn = reader.GetString(4)
                });
            }
        }
    }
}
