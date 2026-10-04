using System.Security.Cryptography;
using System.Text;

namespace Cadence.Core.TaskXml;

/// <summary>Short hash of the canonical projection (never of raw XML): first 16 lowercase hex characters of SHA-256.</summary>
public static class Fingerprint
{
    public static string Of(TaskDefinitionModel model) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Projection.Canonical(model))))[..16];
}
