using System.IO;
using System.Windows;
using System.Windows.DragDrop;

namespace ProPS1Studio.App.Views;

public partial class BatToExeView
{
    public BatToExeView()
    {
        InitializeComponent();
        EnableDragDrop();
    }

    private void EnableDragDrop()
    {
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
    }

    private void SetDropActive(bool active)
    {
        if (DropOverlay is null)
        {
            return;
        }

        DropOverlay.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool HasAcceptableFiles(DragEventArgs e)
    {
        return e.Data.GetDataAsFileDropList()
            .Any(path => path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (HasAcceptableFiles(e))
        {
            e.Effects = DragDropEffects.Copy;
            SetDropActive(true);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            SetDropActive(false);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = HasAcceptableFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropActive(false);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropActive(false);

        var path = e.Data.GetDataAsFileDropList()
            .FirstOrDefault(p => p.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        if (path is null || DataContext is not ProPS1Studio.App.ViewModels.BatToExeViewModel vm)
        {
            return;
        }

        vm.InputPath = path;
    }

    private void CopyOutput_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProPS1Studio.App.ViewModels.BatToExeViewModel vm &&
            !string.IsNullOrWhiteSpace(vm.CompilerOutputText))
        {
            System.Windows.Clipboard.SetText(vm.CompilerOutputText);
        }
    }
}
