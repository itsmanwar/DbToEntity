using DbToEntity.Core;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DbToEntity.Tests;

public class TypeMappingTests
{
    // (column spec type, expected C# property type, expected store type as EF/Npgsql will use it).
    // The store type must equal the real Postgres column type; if Npgsql's default for the C# type
    // differs, TypeMapper must supply an explicit ColumnType.
    public static readonly (string PgType, string Clr, string StoreType)[] Cases =
    {
        ("int2", "Int16", "smallint"),
        ("int4", "Int32", "integer"),
        ("int8", "Int64", "bigint"),
        ("float4", "Single", "real"),
        ("float8", "Double", "double precision"),
        ("numeric", "Decimal", "numeric"),
        ("money", "Decimal", "money"),
        ("bool", "Boolean", "boolean"),
        ("text", "String", "text"),
        ("varchar(50)", "String", "character varying(50)"),
        ("bpchar(3)", "String", "character(3)"),
        ("name", "String", "name"),
        ("citext", "String", "citext"),
        ("json", "String", "json"),
        ("jsonb", "String", "jsonb"),
        ("xml", "String", "xml"),
        ("uuid", "Guid", "uuid"),
        ("bytea", "Byte[]", "bytea"),
        ("date", "DateOnly", "date"),
        ("time", "TimeOnly", "time without time zone"),
        ("timetz", "DateTimeOffset", "time with time zone"),
        ("timestamp", "DateTime", "timestamp without time zone"),
        ("timestamptz", "DateTime", "timestamp with time zone"),
        ("interval", "TimeSpan", "interval"),
        ("inet", "IPAddress", "inet"),
        ("cidr", "NpgsqlCidr", "cidr"),
        ("macaddr", "PhysicalAddress", "macaddr"),
        ("bit(8)", "BitArray", "bit(8)"),
        ("varbit", "BitArray", "bit varying"),
        ("tsvector", "NpgsqlTsVector", "tsvector"),
        ("tsquery", "NpgsqlTsQuery", "tsquery"),
        ("hstore", "Dictionary`2", "hstore"),
        ("point", "NpgsqlPoint", "point"),
        ("line", "NpgsqlLine", "line"),
        ("lseg", "NpgsqlLSeg", "lseg"),
        ("box", "NpgsqlBox", "box"),
        ("path", "NpgsqlPath", "path"),
        ("polygon", "NpgsqlPolygon", "polygon"),
        ("circle", "NpgsqlCircle", "circle"),
        ("int4range", "NpgsqlRange`1", "int4range"),
        ("int8range", "NpgsqlRange`1", "int8range"),
        ("numrange", "NpgsqlRange`1", "numrange"),
        ("daterange", "NpgsqlRange`1", "daterange"),
        ("tsrange", "NpgsqlRange`1", "tsrange"),
        ("tstzrange", "NpgsqlRange`1", "tstzrange"),
        ("oid", "UInt32", "oid"),
        ("xid", "UInt32", "xid"),
        // arrays
        ("_int4", "Int32[]", "integer[]"),
        ("_text", "String[]", "text[]"),
        ("_uuid", "Guid[]", "uuid[]"),
        ("_timestamp", "DateTime[]", "timestamp without time zone[]"),
        ("_jsonb", "String[]", "jsonb[]"),
    };

    [Fact]
    public void EveryMapping_ProducesTheCorrectClrAndStoreType()
    {
        var s = new SchemaBuilder();
        var columns = Cases.Select((c, i) => $"c{i}:{c.PgType}").Prepend("*id").ToArray();
        s.Table("all_types", "AllType", columns);

        var entity = GeneratedModel.Build(s).Entity("AllType");

        var mismatches = Cases.Select((c, i) =>
        {
            var p = entity.FindProperty($"C{i}") ?? throw new Xunit.Sdk.XunitException($"Property for {c.PgType} missing");
            var clr = p.ClrType.IsArray ? p.ClrType.GetElementType()!.Name + "[]" : p.ClrType.Name;
            var store = p.GetColumnType();
            return clr == c.Clr && store == c.StoreType ? null : $"{c.PgType}: expected {c.Clr}/{c.StoreType}, got {clr}/{store}";
        }).Where(m => m != null).ToList();

        Assert.Empty(mismatches);
    }

    [Fact]
    public void EverySupportedType_IsCoveredByTheTable()
    {
        var tested = Cases.Select(c => c.PgType.TrimStart('_').Split('(')[0]).ToHashSet();
        Assert.Equal(Array.Empty<string>(), TypeMapper.SupportedTypes.Where(t => !tested.Contains(t)));
    }

    [Fact]
    public void NullableValueTypes_AreNullable_ReferenceTypesAreNot()
    {
        var s = new SchemaBuilder();
        var t = s.Table("t", "T", "*id", "a:int4?", "b:text?", "c:_int4?", "d:timestamp?");

        GeneratedModel.Build(s);

        Assert.Equal(new[] { "int", "int?", "string", "int[]", "DateTime?" }, t.Columns.Select(c => c.ClrType));
    }

    [Fact]
    public void Enums_GenerateCSharpEnum_AndMapToThePostgresType()
    {
        var s = new SchemaBuilder();
        s.Enum("public", "mood", "happy", "very sad", "ok");
        s.Enum("sales", "order_status", "pending", "shipped");
        s.Table("people", "Person", "*id", "mood:public.mood", "mood_history:public._mood?", "last_status:sales.order_status?");

        var model = GeneratedModel.Build(s);
        var entity = model.Entity("Person");

        Assert.Equal("Mood", entity.FindProperty("Mood")!.ClrType.Name);
        Assert.Equal("public.mood", entity.FindProperty("Mood")!.GetColumnType()); // schema-qualified: independent of search_path
        Assert.Equal("public.mood[]", entity.FindProperty("MoodHistory")!.GetColumnType());
        Assert.Equal(typeof(Nullable<>), entity.FindProperty("LastStatus")!.ClrType.GetGenericTypeDefinition());
        Assert.Equal("sales.order_status", entity.FindProperty("LastStatus")!.GetColumnType());

        // Labels that aren't valid identifiers keep their database spelling through [PgName].
        Assert.Contains("[PgName(\"very sad\")]", model.Sources["Mood.cs"]);
        Assert.Contains("VerySad", model.Sources["Mood.cs"]);
    }

    [Fact]
    public void EnumNames_AvoidCollisions()
    {
        var s = new SchemaBuilder();
        s.Table("statuses", "Status", "*id");
        var status = s.Enum("public", "status", "a");
        var publicKind = s.Enum("public", "kind", "a");
        var salesKind = s.Enum("sales", "kind", "a");

        EntityNaming.ResolveEnumNames(s.Enums, s.Tables);

        Assert.Equal("StatusEnum", status.ClassName);
        Assert.Equal("PublicKind", publicKind.ClassName);
        Assert.Equal("SalesKind", salesKind.ClassName);
    }

    [Fact]
    public void UnsupportedColumns_AreSkippedWithDependentKeysIndexesAndForeignKeys()
    {
        var s = new SchemaBuilder();
        var shapes = s.Table("shapes", "Shape", "*geom:geometry", "label:text");
        var places = s.Table("places", "Place", "*id", "shape_geom:geometry?", "name:text");
        shapes.Indexes.Add(new() { Name = "ix_shape_geom", Columns = { "geom" } });
        s.ForeignKey("fk_place_shape", places, "shape_geom", shapes, "geom");

        var model = GeneratedModel.Build(s);

        Assert.Equal(new[] { "label" }, shapes.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "id", "name" }, places.Columns.Select(c => c.Name));
        Assert.Empty(shapes.Indexes);
        Assert.Empty(places.ForeignKeys);
        Assert.Empty(shapes.ReferencingForeignKeys);
        Assert.Null(model.Entity("Shape").FindPrimaryKey()); // PK column was unsupported → keyless
        Assert.Contains(model.Warnings, w => w.Contains("shapes.geom") && w.Contains("geometry"));
        Assert.Contains(model.Warnings, w => w.Contains("fk_place_shape"));
    }
}
