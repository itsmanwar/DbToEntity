using System;
using System.Collections.Generic;
using System.Linq;
using DbToEntity.Core.Models;

namespace DbToEntity.Core
{
    public static class TypeMapper
    {
        /// <param name="ClrType">C# type without nullability marker.</param>
        /// <param name="IsValueType">Whether a nullable column needs a <c>?</c> suffix.</param>
        /// <param name="ColumnType">Explicit store type for <c>HasColumnType</c>, or null when Npgsql's default
        /// mapping for <paramref name="ClrType"/> already produces the right Postgres type.</param>
        public record Mapping(string ClrType, bool IsValueType, string? ColumnType = null);

        // Keyed by Postgres udt_name. Every entry is checked by DbToEntity.Tests against the store type
        // Npgsql actually infers, so a missing ColumnType shows up as a test failure.
        private static readonly Dictionary<string, Mapping> BuiltIn = new()
        {
            ["int2"] = new("short", true),
            ["int4"] = new("int", true),
            ["int8"] = new("long", true),
            ["float4"] = new("float", true),
            ["float8"] = new("double", true),
            ["numeric"] = new("decimal", true),
            ["money"] = new("decimal", true, "money"),
            ["bool"] = new("bool", true),

            ["text"] = new("string", false),
            ["varchar"] = new("string", false), // length comes from HasMaxLength
            ["bpchar"] = new("string", false, "character"), // length appended in Resolve
            ["name"] = new("string", false, "name"),
            ["citext"] = new("string", false, "citext"),
            ["json"] = new("string", false, "json"),
            ["jsonb"] = new("string", false, "jsonb"),
            ["xml"] = new("string", false, "xml"),

            ["uuid"] = new("Guid", true),
            ["bytea"] = new("byte[]", false),

            ["date"] = new("DateOnly", true),
            ["time"] = new("TimeOnly", true),
            ["timetz"] = new("DateTimeOffset", true, "time with time zone"),
            ["timestamp"] = new("DateTime", true, "timestamp without time zone"),
            ["timestamptz"] = new("DateTime", true),
            ["interval"] = new("TimeSpan", true),

            ["inet"] = new("System.Net.IPAddress", false),
            ["cidr"] = new("NpgsqlTypes.NpgsqlCidr", true),
            ["macaddr"] = new("System.Net.NetworkInformation.PhysicalAddress", false),

            ["bit"] = new("System.Collections.BitArray", false, "bit"), // length appended in Resolve
            ["varbit"] = new("System.Collections.BitArray", false),

            ["tsvector"] = new("NpgsqlTypes.NpgsqlTsVector", false),
            ["tsquery"] = new("NpgsqlTypes.NpgsqlTsQuery", false),
            ["hstore"] = new("Dictionary<string, string>", false),

            ["point"] = new("NpgsqlTypes.NpgsqlPoint", true),
            ["line"] = new("NpgsqlTypes.NpgsqlLine", true),
            ["lseg"] = new("NpgsqlTypes.NpgsqlLSeg", true),
            ["box"] = new("NpgsqlTypes.NpgsqlBox", true),
            ["path"] = new("NpgsqlTypes.NpgsqlPath", true),
            ["polygon"] = new("NpgsqlTypes.NpgsqlPolygon", true),
            ["circle"] = new("NpgsqlTypes.NpgsqlCircle", true),

            ["int4range"] = new("NpgsqlTypes.NpgsqlRange<int>", true),
            ["int8range"] = new("NpgsqlTypes.NpgsqlRange<long>", true),
            ["numrange"] = new("NpgsqlTypes.NpgsqlRange<decimal>", true),
            ["daterange"] = new("NpgsqlTypes.NpgsqlRange<DateOnly>", true),
            ["tsrange"] = new("NpgsqlTypes.NpgsqlRange<DateTime>", true, "tsrange"),
            ["tstzrange"] = new("NpgsqlTypes.NpgsqlRange<DateTime>", true),

            ["oid"] = new("uint", true, "oid"),
            ["xid"] = new("uint", true, "xid"), // Npgsql 10 maps uint to bigint by default
        };

        // Types that cannot be array elements in the generated model (Npgsql has no array mapping for them).
        private static readonly HashSet<string> NoArraySupport = new() { "hstore" };

        public static IReadOnlyCollection<string> SupportedTypes => BuiltIn.Keys;

        /// <summary>
        /// Maps a column's Postgres type to a C# type. Returns null when the type is not supported,
        /// in which case the column should be skipped (EF cannot map <c>object</c>).
        /// </summary>
        public static Mapping? Resolve(ColumnMetadata column, IReadOnlyCollection<EnumMetadata> enums)
        {
            var udt = column.DataType;
            var isArray = udt.StartsWith("_", StringComparison.Ordinal);
            var element = isArray ? udt.Substring(1) : udt;

            Mapping? mapping;
            var enumType = enums.FirstOrDefault(e => e.Name == element && e.Schema == column.TypeSchema);
            if (enumType != null)
            {
                // Store type comes from MapEnum<T> in the generated ConfigureNpgsql helper.
                mapping = new Mapping(enumType.ClassName, true);
            }
            else if (BuiltIn.TryGetValue(element, out var builtIn) && !(isArray && NoArraySupport.Contains(element)))
            {
                mapping = builtIn;
                if (builtIn.ColumnType != null && column.MaxLength.HasValue && (element == "bpchar" || element == "bit"))
                    mapping = builtIn with { ColumnType = $"{builtIn.ColumnType}({column.MaxLength.Value})" };
            }
            else
            {
                return null;
            }

            if (isArray)
            {
                // Arrays are reference types; element nullability is not tracked.
                return new Mapping(mapping.ClrType + "[]", false, mapping.ColumnType == null ? null : mapping.ColumnType + "[]");
            }

            return mapping;
        }

        /// <summary>
        /// Sets <see cref="ColumnMetadata.ClrType"/> and <see cref="ColumnMetadata.ColumnType"/> on every column.
        /// Columns of unsupported types are removed together with the keys, indexes and foreign keys that
        /// reference them. Returns a warning per removed column.
        /// </summary>
        public static List<string> ResolveColumnTypes(IEnumerable<TableMetadata> tables, IReadOnlyCollection<EnumMetadata> enums)
        {
            var warnings = new List<string>();
            var tableList = tables.ToList();

            foreach (var table in tableList)
            {
                var skipped = new HashSet<string>();
                foreach (var column in table.Columns)
                {
                    var mapping = Resolve(column, enums);
                    if (mapping == null)
                    {
                        skipped.Add(column.Name);
                        continue;
                    }

                    column.ClrType = column.IsNullable && mapping.IsValueType ? mapping.ClrType + "?" : mapping.ClrType;
                    column.ColumnType = mapping.ColumnType;
                }

                if (skipped.Count == 0) continue;

                foreach (var name in skipped)
                {
                    var type = table.Columns.First(c => c.Name == name).DataType;
                    warnings.Add($"Skipped column {table.Schema}.{table.Name}.{name}: unsupported type '{type}'.");
                }

                table.Columns.RemoveAll(c => skipped.Contains(c.Name));
                table.Indexes.RemoveAll(i => i.Columns.Any(skipped.Contains));

                if (table.PrimaryKeys.Any(skipped.Contains))
                {
                    warnings.Add($"Table {table.Schema}.{table.Name} has no usable primary key and will be keyless.");
                    table.PrimaryKeys.Clear();
                    table.PrimaryKeyName = null;
                }

                foreach (var fk in table.ForeignKeys.Where(fk => fk.SourceColumns.Any(skipped.Contains)).ToList())
                    RemoveForeignKey(tableList, fk, warnings);
                foreach (var fk in table.ReferencingForeignKeys.Where(fk => fk.TargetColumns.Any(skipped.Contains)).ToList())
                    RemoveForeignKey(tableList, fk, warnings);
            }

            return warnings;
        }

        private static void RemoveForeignKey(List<TableMetadata> tables, ForeignKeyMetadata fk, List<string> warnings)
        {
            foreach (var t in tables)
            {
                t.ForeignKeys.Remove(fk);
                t.ReferencingForeignKeys.Remove(fk);
            }
            warnings.Add($"Dropped foreign key {fk.ConstraintName}: it uses a skipped column.");
        }
    }
}
