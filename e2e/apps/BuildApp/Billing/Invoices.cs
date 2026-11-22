namespace BuildApp.Billing;

public static class Invoices
{
    public static decimal Total(decimal[] lines)
    {
        if (lines.Length == 0) throw new InvalidOperationException("An invoice needs at least one line");
        return lines.Sum();
    }
}
