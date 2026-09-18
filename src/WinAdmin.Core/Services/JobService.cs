using System.Collections.Concurrent;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class JobService
{
    private readonly ConcurrentDictionary<Guid, AdminJob> _jobs = new();

    public IReadOnlyList<AdminJob> Recent =>
        _jobs.Values.OrderByDescending(j => j.StartedAt).Take(25).ToList();

    public AdminJob? Get(Guid id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public AdminJob Start(string name, Func<IProgress<JobProgress>, CancellationToken, Task> work)
    {
        var job = new AdminJob { Name = name };
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            var progress = new Progress<JobProgress>(p =>
            {
                job.Percent = p.Percent;
                job.Message = p.Message;
            });

            try
            {
                await work(progress, CancellationToken.None);
                job.State = "Succeeded";
                job.Percent = 100;
                if (string.IsNullOrWhiteSpace(job.Message) || job.Message == "Starting…")
                {
                    job.Message = "Completed";
                }
            }
            catch (Exception ex)
            {
                job.State = "Failed";
                job.Message = ex.Message;
            }
            finally
            {
                job.CompletedAt = DateTime.Now;
            }
        });

        return job;
    }
}
