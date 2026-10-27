using System;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Windows.Hosting.Discovery;

namespace SoulComputerUse.Mcp;

/// <summary>
/// Forwards session resolution to the owner discovery resolver, unchanged.
/// </summary>
internal sealed class OwnerSessionAccessor : ISessionAccessor
{
    private readonly OwnerSessionResolver _resolver;

    /// <summary>Creates one accessor over the resolver that owns the discovery decision.</summary>
    /// <param name="resolver">The owner discovery resolver.</param>
    /// <exception cref="ArgumentNullException">The resolver is null.</exception>
    internal OwnerSessionAccessor(OwnerSessionResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <inheritdoc />
    public Task<ResolvedSession> ResolveAsync(CancellationToken cancellationToken)
    {
        return _resolver.ResolveAsync(cancellationToken);
    }
}
