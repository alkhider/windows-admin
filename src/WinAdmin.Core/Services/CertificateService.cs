using System.Security.Cryptography.X509Certificates;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class CertificateService
{
    public IReadOnlyList<CertificateItem> ListExpiring(int withinDays = 60)
    {
        var list = new List<CertificateItem>();
        ReadStore(StoreName.My, StoreLocation.CurrentUser, list, withinDays);
        ReadStore(StoreName.My, StoreLocation.LocalMachine, list, withinDays);
        ReadStore(StoreName.Root, StoreLocation.LocalMachine, list, withinDays);
        return list.OrderBy(c => c.NotAfter).ToList();
    }

    private static void ReadStore(StoreName name, StoreLocation location, List<CertificateItem> list, int withinDays)
    {
        try
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates)
            {
                var days = (int)(cert.NotAfter - DateTime.Now).TotalDays;
                if (days <= withinDays)
                {
                    list.Add(new CertificateItem
                    {
                        Subject = cert.Subject,
                        Store = $"{location}/{name}",
                        NotAfter = cert.NotAfter,
                        DaysLeft = days
                    });
                }
            }
        }
        catch
        {
            // ignore
        }
    }
}
