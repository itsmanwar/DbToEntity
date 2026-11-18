using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbToEntity.Core
{
    /// <summary>
    /// Contents of <c>db2entity.json</c>, and also the set of options given explicitly on the command line.
    /// Every member is nullable: null means "not specified here". <see cref="Merge"/> layers them
    /// (command line over file); defaults are applied by the caller.
    /// </summary>
    public class GeneratorSettings
    {
        public const string DefaultFileName = "db2entity.json";

        public string? Connection { get; set; }
        public string? ConnectionStringName { get; set; } // key under ConnectionStrings in appsettings.json
        public string? Schema { get; set; }
        public string? Output { get; set; }
        public string? Namespace { get; set; }
        public string? Context { get; set; }
        public bool? SeparateBySchema { get; set; }
        public bool? Pluralize { get; set; }
        public bool? FluentOnly { get; set; }
        public List<string>? Tables { get; set; }
        public List<string>? ExcludeTables { get; set; } // glob patterns on "table" or "schema.table"
        public Dictionary<string, string>? ClassNames { get; set; } // "table" or "schema.table" → class name

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a typo is an error, not ignored
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        /// <summary>Values from <paramref name="overrides"/> win; anything it leaves null comes from <paramref name="baseSettings"/>.</summary>
        public static GeneratorSettings Merge(GeneratorSettings? baseSettings, GeneratorSettings overrides)
        {
            var b = baseSettings ?? new GeneratorSettings();
            return new GeneratorSettings
            {
                Connection = overrides.Connection ?? b.Connection,
                ConnectionStringName = overrides.ConnectionStringName ?? b.ConnectionStringName,
                Schema = overrides.Schema ?? b.Schema,
                Output = overrides.Output ?? b.Output,
                Namespace = overrides.Namespace ?? b.Namespace,
                Context = overrides.Context ?? b.Context,
                SeparateBySchema = overrides.SeparateBySchema ?? b.SeparateBySchema,
                Pluralize = overrides.Pluralize ?? b.Pluralize,
                FluentOnly = overrides.FluentOnly ?? b.FluentOnly,
                Tables = overrides.Tables ?? b.Tables,
                ExcludeTables = overrides.ExcludeTables ?? b.ExcludeTables,
                ClassNames = overrides.ClassNames ?? b.ClassNames,
            };
        }

        /// <summary>Parses settings JSON. Throws <see cref="SettingsException"/> with a readable message on bad input.</summary>
        public static GeneratorSettings Parse(string json, string source = DefaultFileName)
        {
            try
            {
                return JsonSerializer.Deserialize<GeneratorSettings>(json, JsonOptions) ?? new GeneratorSettings();
            }
            catch (JsonException ex) when (ex.Message.Contains("could not be mapped") && ex.Path is { Length: > 2 })
            {
                var valid = string.Join(", ", typeof(GeneratorSettings).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)));
                throw new SettingsException($"{source}: unknown setting '{ex.Path.TrimStart('$', '.')}'. Valid settings: {valid}.", ex);
            }
            catch (JsonException ex)
            {
                throw new SettingsException($"{source}: invalid JSON ({ex.Message})", ex);
            }
        }

        /// <summary>Reads the file, or returns null when it doesn't exist.</summary>
        public static GeneratorSettings? Load(string path) =>
            File.Exists(path) ? Parse(File.ReadAllText(path), Path.GetFileName(path)) : null;

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    }

    public class SettingsException : Exception
    {
        public SettingsException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
