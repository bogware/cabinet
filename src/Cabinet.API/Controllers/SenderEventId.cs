using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Cabinet.API.Controllers;

/// <summary>
/// Turns an ID a sender chose into the stored event ID. Storage ignores an ID it already has, so
/// without this one sender could claim IDs another will use and make their events disappear.
/// Mixing in who sent it keeps each sender's IDs apart, while the same sender re-sending the same
/// ID still collapses to one event.
/// </summary>
public static class SenderEventId
{
    /// <summary>The stored ID for an ID this caller sent.</summary>
    public static string For(ClaimsPrincipal? user, string senderId)
    {
        var sender = user?.FindFirst("ClientId")?.Value ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sender}\0{senderId}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
