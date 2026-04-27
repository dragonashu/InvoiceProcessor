namespace InvoiceProcessor.Web.Models;

public class ItemClass
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public int? Level { get; set; }
    public bool Active { get; set; } = true;
}
