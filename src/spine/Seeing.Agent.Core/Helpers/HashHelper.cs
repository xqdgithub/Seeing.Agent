using System.Security.Cryptography;
using System.Text;

namespace Seeing.Agent.Core.Helpers;

public static class HashHelper
{
    public static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
