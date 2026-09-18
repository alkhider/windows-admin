using System.Data.OleDb;
using System.Globalization;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class SearchIndexService
{
    private const int MaxResults = 200;
    private const string ConnectionString =
        "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    public Task<IReadOnlyList<IndexedFileItem>> SearchAsync(string query, string? aqsExtra = null, string? scopePath = null) =>
        StaRunner.RunAsync(() => Search(query, aqsExtra, scopePath));

    public Task<IReadOnlyList<IndexedFileItem>> GetLargestFilesAsync(int count = 50) =>
        StaRunner.RunAsync(() => QuerySql(
            $@"SELECT TOP {Math.Clamp(count, 1, MaxResults)} System.ItemPathDisplay, System.Size, System.DateModified, System.Kind
               FROM SystemIndex
               WHERE SCOPE='file:' AND System.Size > 1 AND System.ItemType <> 'Directory'
               ORDER BY System.Size DESC"));

    public Task<IndexerStatus> GetStatusAsync() => StaRunner.RunAsync(GetStatus);

    private static IReadOnlyList<IndexedFileItem> Search(string query, string? aqsExtra, string? scopePath)
    {
        var sql = BuildSql(query, aqsExtra, scopePath);
        if (sql is null)
        {
            return [];
        }

        return QuerySql(sql);
    }

    private static string? BuildSql(string query, string? aqsExtra, string? scopePath = null)
    {
        var text = (query ?? "").Trim();
        var extra = (aqsExtra ?? "").Trim();
        if (text.Length == 0 && extra.Length == 0)
        {
            return null;
        }

        var scope = "SCOPE='file:'";
        if (!string.IsNullOrWhiteSpace(scopePath) && Directory.Exists(scopePath))
        {
            var uri = "file:" + Path.GetFullPath(scopePath).Replace('\\', '/').TrimEnd('/');
            scope = $"SCOPE='{uri.Replace("'", "''")}'";
        }

        var where = new StringBuilder(scope);
        if (extra.Length > 0)
        {
            where.Append(" AND ").Append(AqsToSql(extra));
        }

        if (text.Length > 0)
        {
            where.Append(" AND ").Append(AqsToSql(text));
        }

        return $@"SELECT TOP {MaxResults} System.ItemPathDisplay, System.Size, System.DateModified, System.Kind
                  FROM SystemIndex
                  WHERE {where}
                  ORDER BY System.DateModified DESC";
    }

    private static string AqsToSql(string raw)
    {
        var size = Regex.Match(raw, @"size\s*:\s*(?<op>>=|<=|>|<)?\s*(?<n>\d+)\s*(?<u>KB|MB|GB|TB)?", RegexOptions.IgnoreCase);
        if (size.Success)
        {
            var n = long.Parse(size.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = size.Groups["u"].Value.ToUpperInvariant();
            var bytes = unit switch
            {
                "KB" => n * 1024,
                "MB" => n * 1024 * 1024,
                "GB" => n * 1024 * 1024 * 1024,
                "TB" => n * 1024L * 1024 * 1024 * 1024,
                _ => n
            };
            var op = string.IsNullOrEmpty(size.Groups["op"].Value) ? ">" : size.Groups["op"].Value;
            return $"System.Size {op} {bytes}";
        }

        if (Regex.IsMatch(raw, @"datemodified\s*:\s*this\s*week", RegexOptions.IgnoreCase))
        {
            return "System.DateModified >= DATEADD(DAY, -7, GETGMTDATE())";
        }

        if (Regex.IsMatch(raw, @"datemodified\s*:\s*today", RegexOptions.IgnoreCase))
        {
            return "System.DateModified >= DATEADD(DAY, -1, GETGMTDATE())";
        }

        var kind = Regex.Match(raw, @"kind\s*:\s*(?<k>[a-z]+)", RegexOptions.IgnoreCase);
        if (kind.Success)
        {
            var k = EscapeLiteral(kind.Groups["k"].Value);
            return $"CONTAINS(*, 'kind:{k}')";
        }

        return $"CONTAINS(*, '{EscapeContains(raw)}')";
    }

    private static string EscapeContains(string value)
    {
        var trimmed = value.Replace("'", "''").Trim();
        if (trimmed.Contains(':') || trimmed.Contains('*') || trimmed.StartsWith('"'))
        {
            return trimmed;
        }

        return $"\"{trimmed.Replace("\"", "")}*\"";
    }

    private static string EscapeLiteral(string value) =>
        Regex.Replace(value, @"[^a-zA-Z0-9]+", "");

    private static IReadOnlyList<IndexedFileItem> QuerySql(string sql)
    {
        EnsureSearchService();
        try
        {
            return ReadOleDb(sql);
        }
        catch
        {
            return [];
        }
    }

    private static List<IndexedFileItem> ReadOleDb(string sql)
    {
        var list = new List<IndexedFileItem>();
        using var conn = new OleDbConnection(ConnectionString);
        conn.Open();
        using var cmd = new OleDbCommand(sql, conn) { CommandTimeout = 30 };
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && list.Count < MaxResults)
        {
            var path = reader[0]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(path) || path is "C:" or "C:\\")
            {
                continue;
            }

            long size = 0;
            if (reader.FieldCount > 1 && reader[1] is not DBNull && reader[1] is not null)
            {
                try { size = Convert.ToInt64(reader[1], CultureInfo.InvariantCulture); } catch { /* ignore */ }
            }

            DateTime? modified = null;
            if (reader.FieldCount > 2 && reader[2] is not DBNull && reader[2] is not null)
            {
                try { modified = Convert.ToDateTime(reader[2], CultureInfo.InvariantCulture); } catch { /* ignore */ }
            }

            var kind = reader.FieldCount > 3 ? reader[3]?.ToString() ?? "" : "";
            list.Add(new IndexedFileItem { Path = path, SizeBytes = size, Modified = modified, Kind = kind });
        }

        return list;
    }

    private static IndexerStatus GetStatus()
    {
        var service = ReadService();
        EnsureSearchService();
        try
        {
            var probe = ReadOleDb("SELECT TOP 1 System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:'");
            return new IndexerStatus
            {
                Available = true,
                QueryOk = true,
                Service = service,
                Status = probe.Count > 0 ? "Connected to SystemIndex" : "Connected, catalog empty or still crawling"
            };
        }
        catch (Exception ex)
        {
            return new IndexerStatus
            {
                Available = false,
                QueryOk = false,
                Service = service,
                Status = "Not connected",
                LastError = ex.Message
            };
        }
    }

    private static string ReadService()
    {
        try
        {
            using var sc = new ServiceController("WSearch");
            return $"{sc.DisplayName}: {sc.Status}";
        }
        catch
        {
            return "Windows Search service not found";
        }
    }

    private static void EnsureSearchService()
    {
        try
        {
            using var sc = new ServiceController("WSearch");
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
            {
                if (sc.Status == ServiceControllerStatus.Paused)
                {
                    sc.Continue();
                }
                else
                {
                    sc.Start();
                }

                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(12));
            }
        }
        catch
        {
            // may need elevation
        }
    }
}
