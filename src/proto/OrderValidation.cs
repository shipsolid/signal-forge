namespace OrderContracts;

// Single source of truth for order validation limits. gateway-api validates
// client-side (fail fast, avoid a round trip for input order-api would reject
// anyway); order-api re-validates the same limits authoritatively. Both
// compile this file directly via a relative <Compile Include> (see each
// .csproj) instead of duplicating the literals — same idiom as this
// directory's shared orders.proto.
public static class OrderLimits
{
    public const double MaxAmount = 999_999.99;
    public const int MaxDescriptionLength = 500;
}
