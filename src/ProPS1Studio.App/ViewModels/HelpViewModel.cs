using System.IO;
using ProPS1Studio.App.Mvvm;
using ProPS1Studio.App.Services;
using ProPS1Studio.Core.Logging;

namespace ProPS1Studio.App.ViewModels;

/// <summary>Usage guidance, links, and about info.</summary>
public sealed class HelpViewModel : ObservableObject
{
    private readonly AppServices _services;

    public HelpViewModel(AppServices services)
    {
        _services = services;
        OpenPs2exeCommand = new RelayCommand(() => OpenUrl("https://github.com/MScholtes/PS2EXE"));
        OpenUpxCommand = new RelayCommand(() => OpenUrl("https://github.com/upx/upx"));
        OpenDocsCommand = new RelayCommand(OpenLocalDocs);
    }

    public RelayCommand OpenPs2exeCommand { get; }
    public RelayCommand OpenUpxCommand { get; }
    public RelayCommand OpenDocsCommand { get; }

    public string VersionText =>
        "ProPS1 Studio " +
        (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    private void OpenUrl(string url)
    {
        try
        {
            _services.Shell.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _services.Logger.Write(LogLevel.Warn, nameof(HelpViewModel), $"Could not open '{url}': {ex.Message}");
        }
    }

    private void OpenLocalDocs()
    {
        var doc = Path.Combine(AppContext.BaseDirectory, "docs", "user-guide.md");
        if (File.Exists(doc))
        {
            _services.Shell.OpenWithDefaultApplication(doc);
        }
        else
        {
            OpenUrl("https://github.com/MScholtes/PS2EXE");
        }
    }
}
