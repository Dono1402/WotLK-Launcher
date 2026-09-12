namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed partial class ShopUiState
{
    // Keep ItemsSource and its rows stable during an unchanged server poll.
    private static IReadOnlyList<TRow> KeepRows<TRow>(IReadOnlyList<TRow> current, IEnumerable<TRow> next) where TRow : class
    {
        TRow[] rows = next.ToArray();
        return current.Count == rows.Length && current.Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)) ? current : rows;
    }

    private static IReadOnlyList<TRow> ReconcileRows<TData, TRow>(IReadOnlyList<TRow> current, IEnumerable<TData> values,
        Func<TRow, TData> data, Func<TData, TRow> create) where TRow : class => KeepRows(current,
        values.Select(value => current.FirstOrDefault(row => EqualityComparer<TData>.Default.Equals(data(row), value)) ?? create(value)));
}
