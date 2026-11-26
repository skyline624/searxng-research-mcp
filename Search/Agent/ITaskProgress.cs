namespace ServeurIa.Core.Tasks;

public interface ITaskProgress
{
    Task ReportAsync(long done, long total, string? stage = null);
}
