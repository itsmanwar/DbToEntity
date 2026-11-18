using System.Collections.Generic;
using System.IO;
using System.Linq;
using DbToEntity.Core.Models;
using Humanizer;

namespace DbToEntity.Core
{
    /// <summary>
    /// The resolution steps shared by <c>generate</c>, <c>update</c> and the tests. Order matters:
    /// class names → enum names → column types (may drop columns/FKs) → FK class names → navigation names.
    /// </summary>
    public static class ModelPipeline
    {
        /// <summary>
        /// Sets <see cref="TableMetadata.ClassName"/> (users → User). Names that clash across schemas get a
        /// schema prefix (sales.orders → SalesOrder). Must be computed over the same table set every time so
        /// that <c>generate</c> and <c>update</c> agree.
        /// </summary>
        public static void ResolveClassNames(List<TableMetadata> tables)
        {
            static string BaseName(TableMetadata t) => t.Name.Singularize().Pascalize().Replace("_", "");

            var duplicates = tables.GroupBy(BaseName).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var table in tables)
            {
                var baseName = BaseName(table);
                table.ClassName = duplicates.Contains(baseName) ? table.Schema.Pascalize() + baseName : baseName;
            }
        }

        /// <summary>
        /// Runs every step after class names. Returns the enums referenced by the tables' columns and the
        /// warnings produced (e.g. skipped columns of unsupported types).
        /// </summary>
        public static (List<EnumMetadata> UsedEnums, List<string> Warnings) Prepare(List<TableMetadata> tables, List<EnumMetadata> enums)
        {
            EntityNaming.ResolveEnumNames(enums, tables);
            var warnings = TypeMapper.ResolveColumnTypes(tables, enums);
            ResolveForeignKeyClassNames(tables);
            EntityNaming.ResolveNavigationNames(tables);

            var used = enums
                .Where(e => tables.Any(t => t.Columns.Any(c => c.TypeSchema == e.Schema && (c.DataType == e.Name || c.DataType == "_" + e.Name))))
                .ToList();
            return (used, warnings);
        }

        // Fills Target/Source class names on every FK from the resolved table class names.
        private static void ResolveForeignKeyClassNames(List<TableMetadata> tables)
        {
            var tableByQualifiedName = tables.ToDictionary(t => $"{t.Schema}.{t.Name}", t => t);

            foreach (var table in tables)
            {
                foreach (var fk in table.ForeignKeys)
                {
                    fk.TargetClassName = tableByQualifiedName.TryGetValue($"{fk.TargetSchema}.{fk.TargetTable}", out var target)
                        ? target.ClassName
                        : fk.TargetTable.Singularize().Pascalize(); // target not loaded
                    fk.TargetClassNamePlural = fk.TargetClassName.Pluralize();
                }

                foreach (var fk in table.ReferencingForeignKeys)
                {
                    // The provider adds the same FK instance to the source's ForeignKeys and the target's
                    // ReferencingForeignKeys, so identity lookup finds the source table across schemas.
                    var source = tables.FirstOrDefault(t => t.ForeignKeys.Contains(fk));
                    fk.SourceClassName = source?.ClassName ?? fk.SourceTable.Singularize().Pascalize();
                }
            }
        }

        /// <summary>
        /// Folder and namespace for a table's entity file. With <paramref name="separateBySchema"/>:
        /// <c>&lt;output&gt;/&lt;Schema&gt;/&lt;Tables|Views|MaterializedViews&gt;/</c> (no schema folder for public).
        /// Also stores the namespace on the table for DbContext usings.
        /// </summary>
        public static (string Directory, string Namespace) GetLocation(TableMetadata table, string outputDirectory, string rootNamespace, bool separateBySchema)
        {
            var directory = outputDirectory;
            var ns = rootNamespace;

            if (separateBySchema)
            {
                if (!string.IsNullOrEmpty(table.Schema) && table.Schema != "public")
                {
                    var schemaFolder = table.Schema.Pascalize();
                    directory = Path.Combine(directory, schemaFolder);
                    ns = $"{ns}.{schemaFolder}";
                }

                var typeFolder = table.Type.ToString().Pluralize(); // Tables, Views, MaterializedViews
                directory = Path.Combine(directory, typeFolder);
                ns = $"{ns}.{typeFolder}";
            }

            table.Namespace = ns;
            return (directory, ns);
        }
    }
}
