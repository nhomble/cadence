using System.Security.Cryptography;

namespace Cadence.Core.Metadata;

/// <summary>A job id is 8 characters from [a-z0-9], random, and immutable for the life of the job (PLAN 3.8).</summary>
public static class JobId
{
    public const int Length = 8;
    const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static string New()
    {
        Span<char> chars = stackalloc char[Length];
        for (int i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static bool IsValid(string? id)
    {
        if (id is null || id.Length != Length) return false;
        foreach (char c in id)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9')) return false;
        return true;
    }
}
