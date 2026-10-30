using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using AlAnsariDiskExplorer.Infrastructure;
using AlAnsariDiskExplorer.Models;
using AlAnsariDiskExplorer.Services;

namespace AlAnsariDiskExplorer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DeletionLog _deletionLog = new();
    private CancellationTokenSource? _cancellation;
    private DriveItem? _selectedDrive;
    private string _rootPath = string.Empty;
    private bool _isScanning;
    private bool _showProtected = true;
    private string _statusMessage = "Choose a drive or folder, then click Scan.";
    private string _progressText = string.Empty;
    private string _totalFilesText = "-";
    private string _totalSizeText = "-";
    private string _totalFoldersText = "-";
    private string _freeSpaceText = "-";
    private string _selectionText = "No file selected";
    private string _logText = string.Empty;
    private long _totalBytes;
    private long _totalFiles;
    private readonly HashSet<string> _streamedFiles = new(StringComparer.OrdinalIgnoreCase);
    private long _lastFolderUiUpdate;

    public MainViewModel()
    {
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = o => ShowProtected || o is FileEntry { IsProtected: false };

        FoldersView = CollectionViewSource.GetDefaultView(Folders);
        FoldersView.Filter = o => ShowProtected || o is FolderEntry { IsProtected: false };

        RefreshDrivesCommand = new RelayCommand(LoadDrives);
        BrowseCommand = new RelayCommand(Browse);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning);
        CancelCommand = new RelayCommand(Cancel, () => IsScanning);
        OpenRecycleBinCommand = new RelayCommand(OpenRecycleBin);
        RefreshLogCommand = new RelayCommand(RefreshLog);

        LoadDrives();
        RefreshLog();
    }

    public ObservableCollection<DriveItem> Drives { get; } = new();
    public ObservableCollection<FileEntry> Files { get; } = new();
    public ObservableCollection<FolderEntry> Folders { get; } = new();
    public ICollectionView FilesView { get; }
    public ICollectionView FoldersView { get; }

    public RelayCommand RefreshDrivesCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenRecycleBinCommand { get; }
    public RelayCommand RefreshLogCommand { get; }

    public DriveItem? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (!SetProperty(ref _selectedDrive, value)) return;
            if (value is not null) RootPath = value.RootPath;
        }
    }

    public string RootPath
    {
        get => _rootPath;
        set => SetProperty(ref _rootPath, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value)) return;
            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ShowProtected
    {
        get => _showProtected;
        set
        {
            if (!SetProperty(ref _showProtected, value)) return;
            FilesView.Refresh();
            FoldersView.Refresh();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string TotalFilesText
    {
        get => _totalFilesText;
        private set => SetProperty(ref _totalFilesText, value);
    }

    public string TotalSizeText
    {
        get => _totalSizeText;
        private set => SetProperty(ref _totalSizeText, value);
    }

    public string TotalFoldersText
    {
        get => _totalFoldersText;
        private set => SetProperty(ref _totalFoldersText, value);
    }

    public string FreeSpaceText
    {
        get => _freeSpaceText;
        private set => SetProperty(ref _freeSpaceText, value);
    }

    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    private void LoadDrives()
    {
        var previous = SelectedDrive?.RootPath;
        Drives.Clear();

        try
        {
            foreach (var drive in DriveInfo.GetDrives()
                         .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                         .OrderBy(d => d.Name))
            {
                Drives.Add(new DriveItem(drive.RootDirectory.FullName, drive.VolumeLabel, drive.TotalSize, drive.AvailableFreeSpace));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Unable to list drives: " + ex.Message;
        }

        var match = Drives.FirstOrDefault(d => string.Equals(d.RootPath, previous, StringComparison.OrdinalIgnoreCase))
                    ?? Drives.FirstOrDefault(d => d.RootPath.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
                    ?? Drives.FirstOrDefault();

        SelectedDrive = match;

        if (string.IsNullOrWhiteSpace(RootPath))
            RootPath = match?.RootPath ?? "C:\\";

        UpdateFreeSpace();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the folder to analyze",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            RootPath = dialog.FolderName;
            UpdateFreeSpace();
        }
    }

    private async Task ScanAsync()
    {
        if (IsScanning) return;

        if (string.IsNullOrWhiteSpace(RootPath) || !Directory.Exists(RootPath))
        {
            StatusMessage = "The selected folder does not exist.";
            return;
        }

        _cancellation = new CancellationTokenSource();
        IsScanning = true;
        Files.Clear();
        Folders.Clear();
        ProgressText = "Starting scan...";
        StatusMessage = "Scanning...";
        _streamedFiles.Clear();
        _lastFolderUiUpdate = 0;

        var progress = new Progress<ScanProgress>(p =>
        {
            _totalFiles = p.FilesScanned;
            _totalBytes = p.BytesScanned;
            TotalFilesText = p.FilesScanned.ToString("N0");
            TotalFoldersText = p.FoldersScanned.ToString("N0");
            TotalSizeText = SizeFormatter.Format(p.BytesScanned);
            ProgressText = $"{p.FilesScanned:N0} files | {SizeFormatter.Format(p.BytesScanned)} | {Shorten(p.CurrentFolder)}";

            foreach (var file in p.TopFiles)
            {
                if (_streamedFiles.Add(file.FullPath)) Files.Add(file);
            }

            if (p.TopFolders.Count > 0 && Environment.TickCount64 - _lastFolderUiUpdate > 900)
            {
                Folders.Clear();
                foreach (var folder in p.TopFolders) Folders.Add(folder);
                FoldersView.Refresh();
                _lastFolderUiUpdate = Environment.TickCount64;
            }
        });

        try
        {
            var token = _cancellation.Token;
            var root = RootPath;
            var includeProtected = ShowProtected;
            var result = await Task.Run(() => DiskScanner.Scan(root, includeProtected, progress, token), token);

            _streamedFiles.Clear();
            Files.Clear();
            foreach (var file in result.Files) Files.Add(file);
            Folders.Clear();
            foreach (var folder in result.Folders) Folders.Add(folder);

            _totalFiles = result.TotalFiles;
            _totalBytes = result.TotalBytes;
            TotalFilesText = result.TotalFiles.ToString("N0");
            TotalFoldersText = result.TotalFolders.ToString("N0");
            TotalSizeText = SizeFormatter.Format(result.TotalBytes);
            SelectionText = "No file selected";
            UpdateFreeSpace();
            FilesView.Refresh();
            FoldersView.Refresh();

            StatusMessage =
                $"Scan complete: {result.TotalFiles:N0} files, {result.TotalFolders:N0} folders, " +
                $"{SizeFormatter.Format(result.TotalBytes)}. {result.Errors:N0} item(s) skipped.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            ProgressText = string.Empty;
            _cancellation?.Dispose();
            _cancellation = null;
            RefreshLog();
        }
    }

    private void Cancel()
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // Ignore.
        }
    }

    public void UpdateSelection(IEnumerable<FileEntry> selection)
    {
        var files = selection.ToList();
        if (files.Count == 0)
        {
            SelectionText = "No file selected";
            return;
        }

        var protectedCount = files.Count(f => f.IsProtected);
        var text = $"{files.Count:N0} file(s) selected | {SizeFormatter.Format(files.Sum(f => f.Size))}";
        if (protectedCount > 0) text += $" | {protectedCount} protected";
        SelectionText = text;
    }

    public async Task<DeleteOutcome> DeleteSelectedAsync(IEnumerable<FileEntry> files, bool permanent)
    {
        var list = files.ToList();
        var outcome = await Task.Run(() => SafeDeleteService.Delete(list, permanent, _deletionLog));

        foreach (var deleted in outcome.DeletedFiles)
        {
            Files.Remove(deleted);
        }

        if (outcome.BytesFreed > 0)
        {
            _totalBytes = Math.Max(0, _totalBytes - outcome.BytesFreed);
            TotalSizeText = SizeFormatter.Format(_totalBytes);
        }

        if (outcome.Deleted > 0)
        {
            _totalFiles = Math.Max(0, _totalFiles - outcome.Deleted);
            TotalFilesText = _totalFiles.ToString("N0");
        }

        UpdateSelection(Array.Empty<FileEntry>());
        UpdateFreeSpace();
        RefreshLog();

        StatusMessage = outcome.Errors.Count == 0
            ? $"{outcome.Deleted:N0} file(s) deleted ({SizeFormatter.Format(outcome.BytesFreed)}). Rescan to refresh folder sizes."
            : $"{outcome.Deleted:N0} file(s) deleted, {outcome.Errors.Count:N0} error(s).";

        return outcome;
    }

    public void OpenLocation(FileEntry file)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{file.FullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "Unable to open the location: " + ex.Message;
        }
    }

    public void OpenFolder(FolderEntry folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder.FullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "Unable to open the folder: " + ex.Message;
        }
    }

    private void OpenRecycleBin()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:RecycleBinFolder",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "Unable to open the Recycle Bin: " + ex.Message;
        }
    }

    public void RefreshLog() => LogText = _deletionLog.Read();

    private void UpdateFreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(RootPath));
            if (string.IsNullOrWhiteSpace(root)) return;

            var drive = new DriveInfo(root);
            FreeSpaceText = $"{SizeFormatter.Format(drive.AvailableFreeSpace)} free of {SizeFormatter.Format(drive.TotalSize)}";
        }
        catch
        {
            FreeSpaceText = "-";
        }
    }

    private static string Shorten(string path) => path.Length <= 80 ? path : path[..77] + "...";
}


