namespace AutoMagic.Domain.Products;

public sealed record ProductOffer(
    string DetailUrl,
    string ImageUrl,
    string Title,
    string PriceCny);
