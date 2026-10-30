using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AlAnsariDiskExplorer.Models;
using AlAnsariDiskExplorer.Services;
using AlAnsariDiskExplorer.ViewModels;

namespace AlAnsariDiskExplorer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel?.UpdateSelection(FileList.SelectedItems.Cast<FileEntry>());
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is FileEntry file)
            ViewModel?.OpenLocation(file);
    }

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is FolderEntry folder)
            ViewModel?.OpenFolder(folder);
    }

    private async void DeleteToRecycle_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;

        var files = FileList.SelectedItems.Cast<FileEntry>().ToList();
        if (files.Count == 0)
        {
            MessageBox.Show("Select at least one file first.", "Disk Explorer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var blocked = files.Where(f => f.IsProtected).ToList();
        if (blocked.Count > 0)
        {
            ShowBlocked(blocked);
            return;
        }

        var total = files.Sum(f => f.Size);
        var answer = MessageBox.Show(
            $"Send {files.Count:N0} file(s) ({SizeFormatter.Format(total)}) to the Recycle Bin?\n\n" +
            "They can be restored from the Recycle Bin if needed.",
            "Confirm deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        var outcome = await viewModel.DeleteSelectedAsync(files, permanent: false);
        ShowErrors(outcome);
    }

    private async void DeletePermanently_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;

        var files = FileList.SelectedItems.Cast<FileEntry>().ToList();
        if (files.Count == 0)
        {
            MessageBox.Show("Select at least one file first.", "Disk Explorer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var blocked = files.Where(f => f.IsProtected).ToList();
        if (blocked.Count > 0)
        {
            ShowBlocked(blocked);
            return;
        }

        var total = files.Sum(f => f.Size);
        var answer = MessageBox.Show(
            $"Permanently delete {files.Count:N0} file(s) ({SizeFormatter.Format(total)})?\n\n" +
            "This bypasses the Recycle Bin. Deleted files cannot be recovered.",
            "Permanent deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        var outcome = await viewModel.DeleteSelectedAsync(files, permanent: true);
        ShowErrors(outcome);
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileEntry file)
            ViewModel?.OpenLocation(file);
        else
            MessageBox.Show("Select a file first.", "Disk Explorer",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowBlocked(IReadOnlyList<FileEntry> blocked)
    {
        var details = string.Join(Environment.NewLine, blocked.Take(8).Select(f => "- " + f.FullPath));
        if (blocked.Count > 8) details += Environment.NewLine + $"... and {blocked.Count - 8} more.";

        MessageBox.Show(
            $"{blocked.Count:N0} protected file(s) are selected. Deletion is blocked for system, " +
            "program and application-data files.\n\n" + details,
            "Protected files",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void ShowErrors(DeleteOutcome outcome)
    {
        if (outcome.Errors.Count == 0) return;

        MessageBox.Show(
            string.Join(Environment.NewLine, outcome.Errors.Take(10)) +
            (outcome.Errors.Count > 10 ? $"{Environment.NewLine}... and {outcome.Errors.Count - 10} more." : string.Empty),
            "Deletion errors",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Al-Ansari Disk Explorer v1.0.0\n" +
            "Find large files and folders, delete safely\n\n" +
            "- Scans any drive or folder and lists the largest files and folders\n" +
            "- Deletions go to the Recycle Bin by default\n" +
            "- System, program and application-data files are shown in red and blocked\n" +
            "- Every deletion is written to a local journal\n\n" +
            "Runs without administrator rights. No telemetry.",
            "About",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
