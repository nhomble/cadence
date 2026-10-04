using Cadence.Core.Metadata;
using Cadence.Mcp;

namespace Cadence.Agent;

/// <summary>Cadence's attribution rules on top of the protocol module's raw <see cref="ClientIdentity"/> (PLAN 12.4.1).</summary>
public static class ClientIdentityExtensions
{
    extension(ClientIdentity c)
    {
        /// <summary>The attribution name: <see cref="Actor.SanitizeName"/> over Title-else-Name ("unknown agent" when there is neither).</summary>
        public string DisplayName => Actor.SanitizeName(c.PreferredName);
    }
}