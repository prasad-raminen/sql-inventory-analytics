using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Infrastructure.Data;

namespace InventoryAnalytics.UnitTests.Infrastructure;

public sealed class SqlErrorTranslatorTests
{
    [Theory]
    [InlineData(50001, typeof(NotFoundException))]
    [InlineData(50002, typeof(NotFoundException))]
    [InlineData(50004, typeof(NotFoundException))]
    [InlineData(50010, typeof(InsufficientStockException))]
    [InlineData(50003, typeof(RequestValidationException))]
    [InlineData(50020, typeof(RequestValidationException))]
    [InlineData(50021, typeof(RequestValidationException))]
    public void Translate_KnownProcedureErrors_MapToDomainExceptions(int errorNumber, Type expectedType)
    {
        DomainException? result = SqlErrorTranslator.Translate(errorNumber, "message from THROW");

        result.Should().NotBeNull().And.BeOfType(expectedType);
        result!.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Translate_InsufficientStock_KeepsDatabaseMessageAndInnerException()
    {
        var inner = new InvalidOperationException("driver error");

        DomainException? result = SqlErrorTranslator.Translate(
            SqlErrorTranslator.InsufficientStock, "Insufficient stock for product 7 in warehouse 1: requested 5, available 2.", inner);

        result.Should().BeOfType<InsufficientStockException>()
            .Which.Message.Should().Contain("requested 5, available 2");
        result!.InnerException.Should().BeSameAs(inner);
    }

    [Theory]
    [InlineData(547)]    // constraint violation: a bug, not a client error
    [InlineData(1205)]   // deadlock victim
    [InlineData(2627)]   // unique key violation
    [InlineData(49999)]
    public void Translate_UnexpectedErrors_ReturnNull(int errorNumber)
    {
        SqlErrorTranslator.Translate(errorNumber, "boom").Should().BeNull();
    }

    [Fact]
    public void OrderLineTable_Create_MatchesSqlTableTypeShape()
    {
        var table = OrderLineTable.Create([new OrderLine(10, 2), new OrderLine(11, 5)]);

        table.Columns.Cast<System.Data.DataColumn>()
            .Select(c => (c.ColumnName, c.DataType))
            .Should().Equal(("ProductId", typeof(int)), ("Quantity", typeof(int)));
        table.Rows.Should().HaveCount(2);
        table.Rows[1]["ProductId"].Should().Be(11);
        table.Rows[1]["Quantity"].Should().Be(5);
    }
}
