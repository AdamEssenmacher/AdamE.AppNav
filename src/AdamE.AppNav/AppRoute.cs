namespace AdamE.AppNav;

/// <summary>
/// Base type for durable semantic application destinations.
/// </summary>
/// <remarks>
/// Route records should carry durable application identity only. They should not carry pages,
/// view models, services, callbacks, native handles, or runtime request provenance.
/// </remarks>
public abstract record AppRoute;
