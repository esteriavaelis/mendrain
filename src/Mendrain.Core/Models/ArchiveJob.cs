using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mendrain.Core.Models;

public enum JobStatus { Ready, Extracting, Completed, Skipped, Failed, Cancelled }

/// <summary>One archive in the Batch Extract queue. Property changes are raised on the thread that makes them.</summary>
public sealed class ArchiveJob : INotifyPropertyChanged
{
    public ArchiveJob(string sourcePath)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        _destinationFolderName = ArchiveNaming.GetDestinationFolderName(SourcePath);
    }

    public string SourcePath { get; }
    public string FileName => Path.GetFileName(SourcePath);

    private string _destinationFolderName;
    /// <summary>Name of this archive's own folder under the batch destination.</summary>
    public string DestinationFolderName { get => _destinationFolderName; set => Set(ref _destinationFolderName, value); }

    private JobStatus _status = JobStatus.Ready;
    public JobStatus Status { get => _status; set => Set(ref _status, value); }

    private string? _message;
    /// <summary>Outcome or error text shown next to the job.</summary>
    public string? Message { get => _message; set => Set(ref _message, value); }

    private double? _progress;
    /// <summary>0..1 while extracting when sizes are known; null when unknown (show an indeterminate bar).</summary>
    public double? Progress { get => _progress; set => Set(ref _progress, value); }

    private ExtractResult? _result;
    public ExtractResult? Result { get => _result; set => Set(ref _result, value); }

    /// <summary>Puts a finished job back into the queue so it can run again.</summary>
    public void Reset()
    {
        Status = JobStatus.Ready;
        Message = null;
        Progress = null;
        Result = null;
    }

    /// <summary>Display name; also used as the accessible name of queue rows.</summary>
    public override string ToString() => FileName;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
