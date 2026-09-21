namespace ServiceTests.UnitTests;

public record DummyData
{
    public DateTimeOffset? StartTime { get; set; }

    public string[]? Tags { get; set; }


    public string? State { get; set; }
    public string Country { get; set; } = string.Empty;
    public string? EventClass { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public bool IsFuture { get; set; }
    public int aInt { get; set; }

}


public record MarketDefaults
{
    public List<Product>? Products { get; set; } = [];
    public CustomerLimits? Limits { get; set; }
    public Selection? Selection { get; set; }
}


public record Product(string Name, string Code, string Description, bool Disable); // some sort of type or rule
public record CustomerLimits(decimal MaxStake, decimal MaxWin);
public record SelectionLimits(decimal? MaxStake, decimal? MaxWin, decimal? MaxPool);
public record Selection(SelectionLimits Limits);
