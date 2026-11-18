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
        /// <param name="reserved">Namespace segments of the generated code (<see cref="ReservedNames"/>). A class
        /// with such a name would be shadowed by the namespace, so it gets an <c>Entity</c> suffix.</param>
        /// <returns>A note per class renamed because of <paramref name="reserved"/>.</returns>
        public static List<string> ResolveClassNames(List<TableMetadata> tables, bool singularize = true, IReadOnlyDictionary<string, string>? overrides = null, IReadOnlySet<string>? reserved = null)
        {
            var notes = new List<string>();
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

            if (reserved != null && reserved.Count > 0)
            {
                var taken = new HashSet<string>(tables.Select(t => t.ClassName));
                foreach (var table in tables.Where(t => reserved.Contains(t.ClassName)))
                {
                    var name = table.ClassName + "Entity";
                    for (var i = 2; taken.Contains(name) || reserved.Contains(name); i++) name = table.ClassName + "Entity" + i;
                    notes.Add($"Class for {table.Schema}.{table.Name} renamed to {name}: '{table.ClassName}' is also a namespace in the generated code. Set classNames in {GeneratorSettings.DefaultFileName} to choose another name.");
                    taken.Add(name);
                    table.ClassName = name;
                }
            }

            if (overrides == null || overrides.Count == 0) return notes;

            foreach (var table in tables)
            {
                // "schema.table" is more specific than "table".
                if (!overrides.TryGetValue($"{table.Schema}.{table.Name}", out var name) && !overrides.TryGetValue(table.Name, out name))
                    continue;
                if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name))
                    throw new SettingsException($"classNames: '{name}' (for table {table.Schema}.{table.Name}) is not a valid C# class name.");
                if (reserved != null && reserved.Contains(name))
                    throw new SettingsException($"classNames: '{name}' (for table {table.Schema}.{table.Name}) is also a namespace in the generated code, so it can't be used as a class name.");
                table.ClassName = name;
            }

            var clash = tables.GroupBy(t => t.ClassName).FirstOrDefault(g => g.Count() > 1);
            if (clash != null)
                throw new SettingsException($"classNames: class name '{clash.Key}' is used by more than one table ({string.Join(", ", clash.Select(t => $"{t.Schema}.{t.Name}"))}).");
            return notes;
        }

        /// <summary>
        /// Every namespace segment the generated code will declare: the root namespace's parts and, with
        /// --separate-by-schema, each schema folder plus Tables/Views/MaterializedViews/Enums. A type with one of
        /// these names is shadowed by the namespace inside the generated files (CS0118), so class and enum
        /// naming avoids them.
        /// </summary>
        public static HashSet<string> ReservedNames(IEnumerable<TableMetadata> tables, IEnumerable<EnumMetadata> enums, string rootNamespace, bool separateBySchema)
        {
            var names = new HashSet<string>(rootNamespace.Split('.', StringSplitOptions.RemoveEmptyEntries));
            if (!separateBySchema) return names;

            names.UnionWith(new[] { "Tables", "Views", "MaterializedViews", "Enums" });
            names.UnionWith(tables.Select(t => t.Schema).Concat(enums.Select(e => e.Schema))
                .Where(s => !string.IsNullOrEmpty(s) && s != "public")
                .Select(s => s.Pascalize()));
            return names;
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
        public static (List<EnumMetadata> UsedEnums, List<string> Warnings) Prepare(List<TableMetadata> tables, List<EnumMetadata> enums, bool pluralize = true, IReadOnlySet<string>? reserved = null)
        {
            EntityNaming.ResolveEnumNames(enums, tables, reserved);
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

        /// <summary>
        /// Assigns the namespace of every table and enum, and the source/target namespaces of every FK, so each
        /// generated file can import the namespaces of the entities and enums it references. Must run before any
        /// file is generated: with --separate-by-schema an entity may reference a class in another namespace.
        /// </summary>
        public static void AssignLocations(List<TableMetadata> tables, IEnumerable<EnumMetadata> enums, string outputDirectory, string rootNamespace, bool separateBySchema)
        {
            foreach (var table in tables) GetLocation(table, outputDirectory, rootNamespace, separateBySchema);
            foreach (var enumType in enums) GetEnumLocation(enumType, outputDirectory, rootNamespace, separateBySchema);

            var principalByFk = tables.SelectMany(t => t.ReferencingForeignKeys.Select(fk => (fk, t))).ToDictionary(x => x.fk, x => x.t);
            foreach (var table in tables)
            {
                foreach (var fk in table.ForeignKeys)
                {
                    fk.SourceNamespace = table.Namespace;
                    fk.TargetNamespace = principalByFk.TryGetValue(fk, out var principal) ? principal.Namespace : string.Empty;
                }
            }
        }

        /// <summary>
        /// Folder and namespace for an enum file. With <paramref name="separateBySchema"/> enums mirror the
        /// table layout: <c>&lt;output&gt;/&lt;Schema&gt;/Enums/</c> (no schema folder for public); otherwise they
        /// sit next to the entities. Also stores the namespace on the enum for usings.
        /// </summary>
        public static (string Directory, string Namespace) GetEnumLocation(EnumMetadata enumType, string outputDirectory, string rootNamespace, bool separateBySchema)
        {
            var directory = outputDirectory;
            var ns = rootNamespace;

            if (separateBySchema)
            {
                if (!string.IsNullOrEmpty(enumType.Schema) && enumType.Schema != "public")
                {
                    var schemaFolder = enumType.Schema.Pascalize();
                    directory = Path.Combine(directory, schemaFolder);
                    ns = $"{ns}.{schemaFolder}";
                }

                directory = Path.Combine(directory, "Enums");
                ns = $"{ns}.Enums";
            }

            enumType.Namespace = ns;
            return (directory, ns);
        }
    }
}
