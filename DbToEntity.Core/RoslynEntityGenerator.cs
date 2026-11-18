using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using DbToEntity.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

using Humanizer;

namespace DbToEntity.Core
{
    public class RoslynEntityGenerator : IEntityGenerator
    {
        public GeneratedFile GenerateEntity(TableMetadata table, string namespaceName)
        {
            var className = table.ClassName; // Use resolved name
            var properties = new List<MemberDeclarationSyntax>();

            // [Table("Name", Schema = "Schema")]
            var tableAttributeArgs = new List<AttributeArgumentSyntax>
            {
                AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Name)))
            };

            if (!string.IsNullOrEmpty(table.Schema) && table.Schema != "public")
            {
                tableAttributeArgs.Add(AttributeArgument(
                    NameEquals(IdentifierName("Schema")),
                    null,
                    LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Schema))));
            }

            var classAttributes = new List<AttributeListSyntax>
            {
                AttributeList(SingletonSeparatedList(
                    Attribute(IdentifierName("Table"), ToAttributeArgumentList(tableAttributeArgs.ToArray()))))
            };

            // [Index("Col1", "Col2", Name = "IndexName", IsUnique = true)]
            foreach (var index in table.Indexes)
            {
                var indexArgs = new List<AttributeArgumentSyntax>();

                // Columns
                foreach (var col in index.Columns)
                {
                    indexArgs.Add(AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(EntityNaming.GetPropertyName(col, className)))));
                }

                // Name
                indexArgs.Add(AttributeArgument(
                    NameEquals(IdentifierName("Name")),
                    null,
                    LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(index.Name))));

                // IsUnique
                if (index.IsUnique)
                {
                    indexArgs.Add(AttributeArgument(
                        NameEquals(IdentifierName("IsUnique")),
                        null,
                        LiteralExpression(SyntaxKind.TrueLiteralExpression)));
                }

                classAttributes.Add(AttributeList(SingletonSeparatedList(
                    Attribute(IdentifierName("Index"), ToAttributeArgumentList(indexArgs.ToArray())))));
            }

            foreach (var col in table.Columns)
            {
                var typeName = string.IsNullOrEmpty(col.ClrType)
                    ? throw new InvalidOperationException($"Column '{table.Name}.{col.Name}' has no resolved type. Call TypeMapper.ResolveColumnTypes before generating.")
                    : col.ClrType;
                var propName = EntityNaming.GetPropertyName(col.Name, className);
                var attributes = new List<AttributeSyntax>();

                // [Column("name")]
                attributes.Add(Attribute(IdentifierName("Column"),
                    AttributeArgumentList(SingletonSeparatedList(
                        AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.Name)))))));

                // [Key]
                if (table.PrimaryKeys.Contains(col.Name))
                {
                    attributes.Add(Attribute(IdentifierName("Key")));
                }

                if (!col.IsNullable && typeName == "string")
                {
                    attributes.Add(Attribute(IdentifierName("Required")));
                }

                if (col.MaxLength.HasValue && typeName == "string")
                {
                    attributes.Add(Attribute(IdentifierName("StringLength"),
                        AttributeArgumentList(SingletonSeparatedList(
                            AttributeArgument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(col.MaxLength.Value)))))));
                }

                var property = PropertyDeclaration(ParseTypeName(typeName), propName)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword));

                if (attributes.Any())
                {
                    property = property.AddAttributeLists(AttributeList(SeparatedList(attributes)));
                }

                property = property.AddAccessorListAccessors(
                    AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                    AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                properties.Add(property);
            }

            // Navigation properties. Names come from EntityNaming.ResolveNavigationNames so they
            // match the fluent configuration emitted by GenerateDbContext.
            foreach (var fk in table.ForeignKeys)
            {
                var sourcePropNameString = string.Join(", ", fk.SourceColumns.Select(c => EntityNaming.GetPropertyName(c, className)));

                var attributes = new List<AttributeSyntax>
                {
                    Attribute(IdentifierName("ForeignKey"),
                        AttributeArgumentList(SingletonSeparatedList(
                            AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(sourcePropNameString))))))
                };

                if (!string.IsNullOrEmpty(fk.InverseNavigationName))
                {
                    attributes.Add(Attribute(IdentifierName("InverseProperty"),
                        AttributeArgumentList(SingletonSeparatedList(
                            AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(fk.InverseNavigationName)))))));
                }

                var navProperty = PropertyDeclaration(ParseTypeName(fk.TargetClassName), RequireName(fk.NavigationName, fk))
                    .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.VirtualKeyword))
                    .AddAttributeLists(AttributeList(SeparatedList(attributes)))
                    .AddAccessorListAccessors(
                        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                        AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                properties.Add(navProperty);
            }

            // Inverse navigation properties: a collection, or a single reference for one-to-one
            foreach (var fk in table.ReferencingForeignKeys)
            {
                var inverseType = fk.IsOneToOne ? fk.SourceClassName : $"ICollection<{fk.SourceClassName}>";
                var collectionProperty = PropertyDeclaration(ParseTypeName(inverseType), RequireName(fk.InverseNavigationName, fk))
                    .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.VirtualKeyword))
                    .AddAttributeLists(AttributeList(SingletonSeparatedList(
                        Attribute(IdentifierName("InverseProperty"),
                            AttributeArgumentList(SingletonSeparatedList(
                                AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(RequireName(fk.NavigationName, fk))))))))))
                    .AddAccessorListAccessors(
                        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                        AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                properties.Add(collectionProperty);
            }


            var classDeclaration = ClassDeclaration(className)
                .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.PartialKeyword))
                .AddAttributeLists(classAttributes.ToArray())
                .AddMembers(properties.ToArray());

            var namespaceDeclaration = NamespaceDeclaration(ParseName(namespaceName))
                .AddMembers(classDeclaration);

            var cu = CompilationUnit()
                .AddUsings(
                    UsingDirective(ParseName("System")),
                    UsingDirective(ParseName("System.Collections.Generic")), // ICollection<> (don't rely on ImplicitUsings)
                    UsingDirective(ParseName("System.ComponentModel.DataAnnotations")),
                    UsingDirective(ParseName("System.ComponentModel.DataAnnotations.Schema")),
                    UsingDirective(ParseName("Microsoft.EntityFrameworkCore")))
                .AddMembers(namespaceDeclaration)
                .NormalizeWhitespace()
                .WithLeadingTrivia(GeneratedHeader);

            return new GeneratedFile
            {
                FileName = $"{className}.cs",
                Content = cu.ToFullString()
            };
        }

        private AttributeArgumentListSyntax ToAttributeArgumentList(AttributeArgumentSyntax[] args)
        {
            return AttributeArgumentList(SeparatedList(args));
        }

        public GeneratedFile GenerateEnum(EnumMetadata enumType, string namespaceName)
        {
            // [PgName] on every member keeps the mapping exact regardless of Npgsql's name translator.
            var members = EntityNaming.GetEnumMemberNames(enumType).Zip(enumType.Labels, (member, label) =>
                EnumMemberDeclaration(member).AddAttributeLists(AttributeList(SingletonSeparatedList(
                    Attribute(ParseName("PgName"), AttributeArgumentList(SingletonSeparatedList(
                        AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(label))))))))));

            var enumDeclaration = EnumDeclaration(enumType.ClassName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddMembers(members.ToArray());

            var cu = CompilationUnit()
                .AddUsings(UsingDirective(ParseName("NpgsqlTypes")))
                .AddMembers(NamespaceDeclaration(ParseName(namespaceName)).AddMembers(enumDeclaration))
                .NormalizeWhitespace()
                .WithLeadingTrivia(GeneratedHeader);

            return new GeneratedFile { FileName = $"{enumType.ClassName}.cs", Content = cu.ToFullString() };
        }

        // EF 9+: enums are registered on the Npgsql options builder. The DbContext can't do this itself because
        // options are built by the caller, so expose a helper to pass to UseNpgsql.
        private static MemberDeclarationSyntax CreateConfigureNpgsqlMethod(string dbContextName, IEnumerable<EnumMetadata> enums)
        {
            var mapCalls = string.Concat(enums.Select(e =>
                $"npgsql.MapEnum<{e.ClassName}>({Literal(e.Name)}, {Literal(e.Schema)});\n"));

            return ParseMemberDeclaration($@"
/// <summary>
/// Registers the PostgreSQL enum types used by this model. Pass it to UseNpgsql:
/// <c>options.UseNpgsql(connectionString, {dbContextName}.ConfigureNpgsql)</c>
/// </summary>
public static void ConfigureNpgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql)
{{
{mapCalls}}}")!;
        }

        public GeneratedFile GenerateDbContext(List<TableMetadata> tables, string namespaceName, string dbContextName, bool separateBySchema = false, IReadOnlyList<EnumMetadata>? enums = null)
        {
            var dbSets = new List<MemberDeclarationSyntax>();

            foreach (var table in tables)
            {
                var className = table.ClassName; // Use resolved name
                var dbSetType = $"DbSet<{className}>";
                var propName = className.Pluralize();

                var property = PropertyDeclaration(ParseTypeName(dbSetType), propName)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword))
                     .AddAccessorListAccessors(
                    AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                    AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                dbSets.Add(property);
            }

            var baseClass = SimpleBaseType(ParseTypeName("DbContext"));

            var constructor = ConstructorDeclaration(dbContextName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(
                    Parameter(Identifier("options"))
                    .WithType(ParseTypeName($"DbContextOptions<{dbContextName}>")))
                .WithInitializer(
                    ConstructorInitializer(SyntaxKind.BaseConstructorInitializer,
                    ArgumentList(SingletonSeparatedList(Argument(IdentifierName("options"))))))
                .WithBody(Block());

            var classDeclaration = ClassDeclaration(dbContextName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.PartialKeyword))
                .AddBaseListTypes(baseClass)
                .AddMembers(constructor)
                .AddMembers(dbSets.ToArray());

            if (enums != null && enums.Count > 0)
            {
                classDeclaration = classDeclaration.AddMembers(CreateConfigureNpgsqlMethod(dbContextName, enums));
            }

            // The provider puts the same FK instance in the principal's ReferencingForeignKeys.
            var principalByFk = tables
                .SelectMany(t => t.ReferencingForeignKeys.Select(fk => (fk, t)))
                .ToDictionary(x => x.fk, x => x.t);

            var statements = new List<StatementSyntax>();
            statements.Add(ExpressionStatement(
                InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, BaseExpression(), IdentifierName("OnModelCreating")))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(IdentifierName("modelBuilder")))))));

            foreach (var table in tables)
            {
                var className = table.ClassName; // Use resolved name

                // entity => { ... }
                var lambdaStatements = new List<StatementSyntax>();

                // .ToTable("name", "schema") or .ToView("name", "schema")
                string mappingMethod = (table.Type == ObjectType.View || table.Type == ObjectType.MaterializedView) ? "ToView" : "ToTable";

                var toTableArgs = new List<ArgumentSyntax> { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Name))) };
                if (!string.IsNullOrEmpty(table.Schema) && table.Schema != "public")
                {
                    toTableArgs.Add(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Schema))));
                }

                lambdaStatements.Add(ExpressionStatement(
                    InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName(mappingMethod)))
                    .WithArgumentList(ArgumentList(SeparatedList(toTableArgs)))));

                // .HasKey(e => e.Id) or .HasNoKey()
                if (table.PrimaryKeys.Any())
                {
                    var keyProps = table.PrimaryKeys.Select(k => EntityNaming.GetPropertyName(k, className));
                    ExpressionSyntax keyExpression;
                    if (keyProps.Count() == 1)
                    {
                        keyExpression = SimpleLambdaExpression(Parameter(Identifier("e")),
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("e"), IdentifierName(keyProps.First())));
                    }
                    else
                    {
                        var anonProps = keyProps.Select(p => AnonymousObjectMemberDeclarator(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("e"), IdentifierName(p))));
                        keyExpression = SimpleLambdaExpression(Parameter(Identifier("e")),
                            AnonymousObjectCreationExpression(SeparatedList(anonProps)));
                    }

                    var hasKeyInvocation = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("HasKey")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(keyExpression))));

                    if (!string.IsNullOrEmpty(table.PrimaryKeyName))
                    {
                        hasKeyInvocation = InvocationExpression(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, hasKeyInvocation, IdentifierName("HasName")))
                            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.PrimaryKeyName))))));
                    }

                    lambdaStatements.Add(ExpressionStatement(hasKeyInvocation));
                }
                else
                {
                    // Keyless Entity (View)
                    lambdaStatements.Add(ExpressionStatement(
                       InvocationExpression(
                           MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("HasNoKey")))));
                }


                // Properties
                foreach (var col in table.Columns)
                {
                    var propName = EntityNaming.GetPropertyName(col.Name, className);
                    var propertyAccess = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("Property")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                            SimpleLambdaExpression(Parameter(Identifier("e")),
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("e"), IdentifierName(propName)))))));

                    ExpressionSyntax currentExpression = propertyAccess;

                    currentExpression = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("HasColumnName")))
                                    .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.Name))))));

                    // Only emitted where Npgsql's default store type for the CLR type differs (e.g. timestamp, jsonb).
                    if (!string.IsNullOrEmpty(col.ColumnType))
                    {
                        currentExpression = InvocationExpression(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("HasColumnType")))
                            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.ColumnType))))));
                    }

                    if (!string.IsNullOrEmpty(col.DefaultValue))
                    {
                        currentExpression = InvocationExpression(
                           MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("HasDefaultValueSql")))
                           .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.DefaultValue))))));
                    }

                    if (!col.IsNullable)
                    {
                        currentExpression = InvocationExpression(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("IsRequired")));
                    }

                    if (col.MaxLength.HasValue && col.ClrType == "string")
                    {
                        currentExpression = InvocationExpression(
                           MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("HasMaxLength")))
                           .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(col.MaxLength.GetValueOrDefault()))))));
                    }

                    lambdaStatements.Add(ExpressionStatement(currentExpression));
                }

                // Relationships
                foreach (var fk in table.ForeignKeys)
                {
                    var navPropName = RequireName(fk.NavigationName, fk);
                    var sourceProps = fk.SourceColumns.Select(c => EntityNaming.GetPropertyName(c, className)).ToList();

                    var hasOne = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("HasOne")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                             SimpleLambdaExpression(Parameter(Identifier("d")),
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("d"), IdentifierName(navPropName)))))));

                    // .WithMany(p => p.Inverse) / .WithOne(p => p.Inverse) pairs the reference with its inverse; a bare
                    // .WithMany() would leave the inverse to be matched by convention as a second relationship.
                    var withManyArgs = string.IsNullOrEmpty(fk.InverseNavigationName)
                        ? ArgumentList()
                        : ArgumentList(SingletonSeparatedList(Argument(
                            SimpleLambdaExpression(Parameter(Identifier("p")),
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("p"), IdentifierName(fk.InverseNavigationName))))));

                    var withMany = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, hasOne, IdentifierName(fk.IsOneToOne ? "WithOne" : "WithMany")))
                        .WithArgumentList(withManyArgs);

                    ExpressionSyntax fkLambdaExpr;
                    if (sourceProps.Count == 1)
                    {
                        fkLambdaExpr = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("d"), IdentifierName(sourceProps[0]));
                    }
                    else
                    {
                        var anonProps = sourceProps.Select(p => AnonymousObjectMemberDeclarator(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("d"), IdentifierName(p))));
                        fkLambdaExpr = AnonymousObjectCreationExpression(SeparatedList(anonProps));
                    }

                    // For one-to-one EF can't infer which side is dependent, so HasForeignKey names it: HasForeignKey<TDependent>.
                    SimpleNameSyntax hasForeignKeyName = fk.IsOneToOne
                        ? GenericName(Identifier("HasForeignKey")).WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(ParseTypeName(className))))
                        : IdentifierName("HasForeignKey");

                    ExpressionSyntax hasForeignKey = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, withMany, hasForeignKeyName))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                             SimpleLambdaExpression(Parameter(Identifier("d")), fkLambdaExpr)))));

                    // EF assumes an FK targets the principal's primary key. When the constraint references other
                    // columns (a unique constraint), name them, keeping confkey order so pairs line up.
                    if (principalByFk.TryGetValue(fk, out var principal) && !principal.PrimaryKeys.SequenceEqual(fk.TargetColumns))
                    {
                        var principalProps = fk.TargetColumns.Select(c => EntityNaming.GetPropertyName(c, principal.ClassName)).ToList();
                        ExpressionSyntax principalLambdaBody = principalProps.Count == 1
                            ? MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("p"), IdentifierName(principalProps[0]))
                            : AnonymousObjectCreationExpression(SeparatedList(principalProps.Select(prop => AnonymousObjectMemberDeclarator(
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("p"), IdentifierName(prop))))));

                        SimpleNameSyntax hasPrincipalKeyName = fk.IsOneToOne
                            ? GenericName(Identifier("HasPrincipalKey")).WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(ParseTypeName(principal.ClassName))))
                            : IdentifierName("HasPrincipalKey");

                        hasForeignKey = InvocationExpression(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, hasForeignKey, hasPrincipalKeyName))
                            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                                SimpleLambdaExpression(Parameter(Identifier("p")), principalLambdaBody)))));
                    }

                    // Always explicit: EF's convention for required FKs is Cascade, which would cascade on the client
                    // even when the database says NO ACTION.
                    var onDelete = InvocationExpression(
                       MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, hasForeignKey, IdentifierName("OnDelete")))
                       .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                           MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("DeleteBehavior"), IdentifierName(GetDeleteBehavior(fk.OnDelete)))))));

                    var hasConstraint = InvocationExpression(
                       MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, onDelete, IdentifierName("HasConstraintName")))
                       .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(fk.ConstraintName))))));

                    lambdaStatements.Add(ExpressionStatement(hasConstraint));
                }

                var entityMethod = GenericName(Identifier("Entity"))
                    .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(ParseTypeName(className))));

                var entityConfig = InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("modelBuilder"), entityMethod))
                    .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                        ParenthesizedLambdaExpression(ParameterList(SingletonSeparatedList(Parameter(Identifier("entity")))), Block(lambdaStatements))))));

                statements.Add(ExpressionStatement(entityConfig));
            }

            // Runs last, so configuration in the user's own partial class file overrides the generated configuration.
            statements.Add(ExpressionStatement(
                InvocationExpression(IdentifierName("OnModelCreatingPartial"))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(IdentifierName("modelBuilder")))))));

            var onModelCreating = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "OnModelCreating")
                .AddModifiers(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword))
                .AddParameterListParameters(Parameter(Identifier("modelBuilder")).WithType(ParseTypeName("ModelBuilder")))
                .WithBody(Block(statements));

            // Implemented (optionally) by the user in a separate file that regeneration never touches.
            var onModelCreatingPartial = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "OnModelCreatingPartial")
                .AddModifiers(Token(SyntaxKind.PartialKeyword))
                .AddParameterListParameters(Parameter(Identifier("modelBuilder")).WithType(ParseTypeName("ModelBuilder")))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

            classDeclaration = classDeclaration.AddMembers(onModelCreating, onModelCreatingPartial);

            var namespaceDeclaration = NamespaceDeclaration(ParseName(namespaceName))
                .AddMembers(classDeclaration);

            var usings = new List<UsingDirectiveSyntax>
            {
                UsingDirective(ParseName("System")),
                UsingDirective(ParseName("System.ComponentModel.DataAnnotations")),
                UsingDirective(ParseName("System.ComponentModel.DataAnnotations.Schema")),
                UsingDirective(ParseName("Microsoft.EntityFrameworkCore"))
            };

            var distinctNamespaces = tables
                .Select(t => t.Namespace)
                .Where(n => !string.IsNullOrEmpty(n) && n != namespaceName)
                .Distinct()
                .OrderBy(n => n);

            foreach (var ns in distinctNamespaces)
            {
                usings.Add(UsingDirective(ParseName(ns)));
            }

            var cu = CompilationUnit()
                .AddUsings(usings.ToArray())
                .AddMembers(namespaceDeclaration)
                .NormalizeWhitespace()
                .WithLeadingTrivia(GeneratedHeader);

            return new GeneratedFile
            {
                FileName = $"{dbContextName}.cs",
                Content = cu.ToFullString()
            };
        }

        public GeneratedFile UpdateDbContext(string existingCode, List<TableMetadata> tables, string dbContextName)
        {
            var tree = CSharpSyntaxTree.ParseText(existingCode);
            var root = tree.GetRoot() as CompilationUnitSyntax;

            if (root == null) throw new Exception("Could not parse existing DbContext");

            var classNode = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == dbContextName);

            if (classNode == null) throw new Exception($"Could not find class {dbContextName} in existing code");

            var newMembers = new List<MemberDeclarationSyntax>();
            bool changed = false;

            foreach (var table in tables)
            {
                var className = table.ClassName; // Use resolved name
                var propName = className.Pluralize();

                // Check if property exists
                var exists = classNode.Members.OfType<PropertyDeclarationSyntax>()
                    .Any(p => p.Identifier.Text == propName);

                if (!exists)
                {
                    var dbSetType = $"DbSet<{className}>";
                    var property = PropertyDeclaration(ParseTypeName(dbSetType), propName)
                        .AddModifiers(Token(SyntaxKind.PublicKeyword))
                         .AddAccessorListAccessors(
                        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                        AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                    newMembers.Add(property);
                    changed = true;
                }
            }

            if (changed)
            {
                var newClassNode = classNode.AddMembers(newMembers.ToArray());
                var newRoot = root.ReplaceNode(classNode, newClassNode).NormalizeWhitespace();

                return new GeneratedFile
                {
                    FileName = $"{dbContextName}.cs",
                    Content = newRoot.ToFullString()
                };
            }

            return new GeneratedFile
            {
                FileName = $"{dbContextName}.cs",
                Content = existingCode
            };
        }

        private string ToPascalCase(string original)
        {
            return original.Pascalize();
        }

        /// <summary>
        /// Maps a Postgres ON DELETE action to the EF DeleteBehavior member name. NO ACTION follows EF's own
        /// scaffolder (ClientSetNull). SET DEFAULT has no EF equivalent, so EF leaves tracked dependents alone
        /// (NoAction) and the database applies the default.
        /// </summary>
        public static string GetDeleteBehavior(ReferentialAction action) => action switch
        {
            ReferentialAction.Cascade => "Cascade",
            ReferentialAction.SetNull => "SetNull",
            ReferentialAction.Restrict => "Restrict",
            ReferentialAction.SetDefault => "NoAction",
            _ => "ClientSetNull"
        };

        // <auto-generated> also makes the compiler treat the file as generated code (e.g. nullable warnings off).
        private static readonly SyntaxTriviaList GeneratedHeader = ParseLeadingTrivia(
            "// <auto-generated>\n" +
            "//     Generated by db2entity. Changes to this file are lost when the code is regenerated.\n" +
            "//     Put customizations in a separate file: partial classes for entities, and\n" +
            "//     'partial void OnModelCreatingPartial(ModelBuilder modelBuilder)' for the DbContext.\n" +
            "// </auto-generated>\n");

        private static string RequireName(string name, ForeignKeyMetadata fk)
        {
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException(
                    $"Navigation names for FK '{fk.ConstraintName}' are not resolved. Call EntityNaming.ResolveNavigationNames before generating.");
            return name;
        }
    }
}
