using DbToEntity.Core.Models;
using DbToEntity.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DbToEntity.Tests;

public class DeleteBehaviorTests
{
    private static DeleteBehavior BuildAndGetDeleteBehavior(ReferentialAction action, bool nullableFk)
    {
        var s = new SchemaBuilder();
        var customer = s.Table("customers", "Customer", "*id");
        var order = s.Table("orders", "Order", "*id", nullableFk ? "customer_id?" : "customer_id");
        s.ForeignKey("fk_order_customer", order, "customer_id", customer, onDelete: action);

        var fk = Assert.Single(GeneratedModel.Build(s).Entity("Order").GetForeignKeys());
        return fk.DeleteBehavior;
    }

    [Theory]
    [InlineData(ReferentialAction.Cascade, false, DeleteBehavior.Cascade)]
    [InlineData(ReferentialAction.Cascade, true, DeleteBehavior.Cascade)]
    [InlineData(ReferentialAction.SetNull, true, DeleteBehavior.SetNull)]
    [InlineData(ReferentialAction.Restrict, false, DeleteBehavior.Restrict)]
    [InlineData(ReferentialAction.Restrict, true, DeleteBehavior.Restrict)]
    [InlineData(ReferentialAction.NoAction, true, DeleteBehavior.ClientSetNull)]
    [InlineData(ReferentialAction.SetDefault, false, DeleteBehavior.NoAction)]
    public void OnDelete_MatchesDatabaseAction(ReferentialAction action, bool nullableFk, DeleteBehavior expected) =>
        Assert.Equal(expected, BuildAndGetDeleteBehavior(action, nullableFk));

    // Regression: EF's convention for a required FK is Cascade. Without an explicit OnDelete, EF would
    // delete tracked dependents even though the database (NO ACTION, the Postgres default) forbids it.
    [Fact]
    public void RequiredFk_WithNoAction_DoesNotCascade() =>
        Assert.Equal(DeleteBehavior.ClientSetNull, BuildAndGetDeleteBehavior(ReferentialAction.NoAction, nullableFk: false));

    [Fact]
    public void EveryRelationship_HasExplicitOnDelete()
    {
        var s = new SchemaBuilder();
        var a = s.Table("a", "A", "*id");
        var b = s.Table("b", "B", "*id", "a_id", "a2_id?");
        s.ForeignKey("fk1", b, "a_id", a);
        s.ForeignKey("fk2", b, "a2_id", a, onDelete: ReferentialAction.SetNull);

        var context = GeneratedModel.Build(s).Sources["AppDbContext.cs"];

        Assert.Contains(".OnDelete(DeleteBehavior.ClientSetNull).HasConstraintName(\"fk1\")", context);
        Assert.Contains(".OnDelete(DeleteBehavior.SetNull).HasConstraintName(\"fk2\")", context);
    }
}
