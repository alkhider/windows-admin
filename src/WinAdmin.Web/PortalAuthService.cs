using System.Security.Cryptography;
using System.Text;

namespace WinAdmin.Web;

public sealed class PortalAuthService
{
    private static readonly byte[] PasswordHash = Convert.FromHexString(
        "746cd1efe63c2cbed80c74b465dd9e6a7bbe74cd84dc3a3715c36d8dd8411261");

    public bool IsSignedIn { get; private set; }
    public static bool ProcessUnlocked => Volatile.Read(ref OpenFlag) == 1;
    private static int OpenFlag;

    public event Action? Changed;

    public bool TrySignIn(string? password)
    {
        var ok = Verify(password);
        if (!ok)
        {
            return false;
        }

        IsSignedIn = true;
        Interlocked.Exchange(ref OpenFlag, 1);
        Changed?.Invoke();
        return true;
    }

    public void SignOut()
    {
        IsSignedIn = false;
        Interlocked.Exchange(ref OpenFlag, 0);
        Changed?.Invoke();
    }

    private static bool Verify(string? password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("WinAdmin.local|" + (password ?? "")));
        return CryptographicOperations.FixedTimeEquals(bytes, PasswordHash);
    }
}
