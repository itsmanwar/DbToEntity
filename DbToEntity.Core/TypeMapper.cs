using System;

namespace DbToEntity.Core
{
    public static class TypeMapper
    {
        public static string Map(string postgresType, bool isNullable)
        {
            string csharpType = postgresType switch
            {
                "integer" or "int" or "int4" => "int",
                "bigint" or "int8" => "long",
                "smallint" or "int2" => "short",
                "boolean" or "bool" => "bool",
                "text" or "varchar" or "character varying" or "char" or "character" or "bpchar" => "string",
                "numeric" or "decimal" or "money" => "decimal",
                "real" or "float4" => "float",
                "double precision" or "float8" => "double",
                "date" => "DateOnly",
                "timestamp" or "timestamp without time zone" => "DateTime",
                "timestamptz" or "timestamp with time zone" => "DateTime",
                "uuid" => "Guid",
                "bytea" => "byte[]",
                "json" or "jsonb" => "string", // Or JsonDocument depending on usage
                _ => "object" // Fallback
            };

            if (isNullable && csharpType != "string" && csharpType != "object" && csharpType != "byte[]")
            {
                return csharpType + "?";
            }

            return csharpType;
        }
    }
}
