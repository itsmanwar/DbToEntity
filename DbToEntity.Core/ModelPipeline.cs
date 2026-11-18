using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        /// <param name="singularize">False keeps the table name's number (--no-pluralize): users → Users.</param>
        /// <param name="overrides">Explicit class names keyed by "table" or "schema.table" (db2entity.json
        /// <c>classNames</c>). Throws <see cref="SettingsException"/> for invalid or clashing names.</param>
        public static void ResolveClassNames(List<TableMetadata> tables, bool singularize = true, IReadOnlyDictionary<string, string>? overrides = null)
        {
            // SanitizeIdentifier: table names may contain characters C# doesn't allow (e.g. "it's", "order-items").
            string BaseName(TableMetadata t)
            {
                var name = EntityNaming.SanitizeIdentifier(singularize ? t.Name.Singularize() : t.Name).Replace("_", "");
                return name.Length == 0 || char.IsDigit(name[0]) ? "_" + name : name; // "2fa_codes" -> _2faCode
            }

            var duplicates = tables.GroupBy(BaseName).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var table in tables)
            {
                var baseName = BaseName(table);
                table.ClassName = duplicates.Contains(baseName) ? table.Schema.Pascalize() + baseName : baseName;
            }

            if (overrides == null || overrides.Count == 0) return;

            foreach (var table in tables)
            {
                // "schema.table" is more specific than "table".
                if (!overrides.TryGetValue($"{table.Schema}.{table.Name}", out var name) && !overrides.TryGetValue(table.Name, out name))
                    continue;
                if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name))
                    throw new SettingsException($"classNames: '{name}' (for table {table.Schema}.{table.Name}) is not a valid C# class name.");
                table.ClassName = name;
            }

            var clash = tables.GroupBy(t => t.ClassName).FirstOrDefault(g => g.Count() > 1);
            if (clash != null)
                throw new SettingsException($"classNames: class name '{clash.Key}' is used by more than one table ({string.Join(", ", clash.Select(t => $"{t.Schema}.{t.Name}"))}).");
        }

        /// <summary>Tables that are never generated unless requested by name: EF Core's migration history.</summary>
        public static readonly IReadOnlyList<string> AlwaysExcluded = new[] { "__EFMigrationsHistory" };

        /// <summary>
        /// Removes tables matching any glob pattern (<c>*</c>, <c>?</c>) on "table" or "schema.table", plus
        /// <see cref="AlwaysExcluded"/>, and drops foreign keys to or from them. Tables named in
        /// <paramref name="keep"/> (explicit --tables) are never excluded. Returns the removed tables' names.
        /// </summary>
        public static List<string> ExcludeTables(List<TableMetadata> tables, IEnumerable<string>? patterns, IEnumerable<string>? keep = null)
        {
            var regexes = (patterns ?? Enumerable.Empty<string>()).Concat(AlwaysExcluded)
                .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase))
                .ToList();
            var kept = new HashSet<string>(keep ?? Enumerable.Empty<string>());

            var removed = tables
                .Where(t => !kept.Contains(t.Name) && regexes.Any(r => r.IsMatch(t.Name) || r.IsMatch($"{t.Schema}.{t.Name}")))
                .ToList();
            if (removed.Count == 0) return new List<string>();

            var removedSet = new HashSet<TableMetadata>(removed);
            var removedFks = removed.SelectMany(t => t.ForeignKeys.Concat(t.ReferencingForeignKeys)).ToHashSet();
            tables.RemoveAll(removedSet.Contains);
            foreach (var table in tables)
            {
                table.ForeignKeys.RemoveAll(removedFks.Contains);
                table.ReferencingForeignKeys.RemoveAll(removedFks.Contains);
            }

            return removed.Select(t => $"{t.Schema}.{t.Name}").ToList();
        }

        /// <summary>
        /// Runs every step after class names. Returns the enums referenced by the tables' columns and the
        /// warnings produced (e.g. skipped columns of unsupported types).
        /// </summary>
        /// <param name="pluralize">False (--no-pluralize) keeps DbSet and collection names unpluralized.</param>
        public static (List<EnumMetadata> UsedEnums, List<string> Warnings) Prepare(List<TableMetadata> tables, List<EnumMetadata> enums, bool pluralize = true)
        {
            EntityNaming.ResolveEnumNames(enums, tables);
            var warnings = TypeMapper.ResolveColumnTypes(tables, enums);
            ResolveForeignKeyClassNames(tables);
            EntityNaming.ResolveNavigationNames(tables, pluralize);
            ResolveDbSetNames(tables, pluralize);

            var used = enums
                .Where(e => tables.Any(t => t.Columns.Any(c => c.TypeSchema == e.Schema && (c.DataType == e.Name || c.DataType == "_" + e.Name))))
                .ToList();
            return (used, warnings);
        }

        // DbContext members a DbSet must not hide (e.g. a table named "database" with --no-pluralize).
        private static readonly HashSet<string> DbContextMembers = new() { "Database", "Model", "ChangeTracker", "ContextId" };

        private static void ResolveDbSetNames(List<TableMetadata> tables, bool pluralize)
        {
            var used = new HashSet<string>(DbContextMembers);
            foreach (var table in tables)
            {
                var name = pluralize ? table.ClassName.Pluralize() : table.ClassName;
                var candidate = name;
                for (var i = 1; used.Contains(candidate); i++) candidate = i == 1 ? name + "Set" : name + "Set" + i;
                used.Add(candidate);
                table.DbSetName = candidate;
            }
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
                        : EntityNaming.SanitizeIdentifier(fk.TargetTable.Singularize()); // target not loaded
                    fk.TargetClassNamePlural = fk.TargetClassName.Pluralize();
                }

                foreach (var fk in table.ReferencingForeignKeys)
                {
                    // The provider adds the same FK instance to the source's ForeignKeys and the target's
                    // ReferencingForeignKeys, so identity lookup finds the source table across schemas.
                    var source = tables.FirstOrDefault(t => t.ForeignKeys.Contains(fk));
                    fk.SourceClassName = source?.ClassName ?? EntityNaming.SanitizeIdentifier(fk.SourceTable.Singularize());
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
