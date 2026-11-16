using System.Data;
using InventoryAnalytics.Core.Models;

namespace InventoryAnalytics.Infrastructure.Data;

/// <summary>Builds the table-valued parameter for <c>dbo.OrderLineList</c>.</summary>
public static class OrderLineTable
{
    public const string TypeName = "dbo.OrderLineList";

    /// <summary>Columns must match the SQL type's column order: (ProductId INT, Quantity INT).</summary>
    public static DataTable Create(IEnumerable<OrderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var table = new DataTable();
        table.Columns.Add("ProductId", typeof(int));
        table.Columns.Add("Quantity", typeof(int));

        foreach (OrderLine line in lines)
        {
            table.Rows.Add(line.ProductId, line.Quantity);
        }

        return table;
    }
}
