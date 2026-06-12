using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PatternConversionTool.Models;
using PatternConversionTool.Services;

namespace PatternConversionTool.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IStilParser _parser;
    private readonly ISignalConfigService _csvService;

    private StilParseResult? _parseResult;
    private string? _stilFilePath;

    // Running-status log. New entries are appended (not overwritten) so the
    // panel keeps the full history rather than just the latest message.
    private readonly System.Text.StringBuilder _statusLog = new();
    private string _statusLogText = "";

    private bool _progressVisible;
    private double _progressValue;
    private double _progressMaximum = 1;
    private string _progressText = "";

    public ObservableCollection<Signal> Signals { get; } = new();

    /// <summary>
    /// Running-status history shown in the status panel. Bound read-only to the
    /// UI; entries are added through <see cref="AppendStatus"/>.
    /// </summary>
    public string StatusLog
    {
        get => _statusLogText;
        private set { _statusLogText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Sets the latest status message. Retained for call-site compatibility;
    /// each assignment is appended to <see cref="StatusLog"/> as a new entry
    /// instead of overwriting the previous one.
    /// </summary>
    public string StatusText
    {
        set => AppendStatus(value);
    }

    /// <summary>
    /// Appends a timestamped line to the running-status log. New messages are
    /// added to the history and the UI scrolls to the latest entry.
    /// </summary>
    public void AppendStatus(string message)
    {
        if (_statusLog.Length > 0)
            _statusLog.AppendLine();
        _statusLog.Append($"[{DateTime.Now:HH:mm:ss}] {message}");
        StatusLog = _statusLog.ToString();
    }

    /// <summary>Whether the progress indicator is shown (only during generation).</summary>
    public bool ProgressVisible
    {
        get => _progressVisible;
        set { _progressVisible = value; OnPropertyChanged(); }
    }

    /// <summary>Current progress value (number of lines processed).</summary>
    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    /// <summary>Total number of lines to process.</summary>
    public double ProgressMaximum
    {
        get => _progressMaximum;
        set { _progressMaximum = value; OnPropertyChanged(); }
    }

    /// <summary>Human-readable progress label, e.g. "line 1234/56789".</summary>
    public string ProgressText
    {
        get => _progressText;
        set { _progressText = value; OnPropertyChanged(); }
    }

    // ?? commands ????????????????????????????????????????????????????
    public ICommand LoadStilCommand  { get; }
    public ICommand LoadCsvCommand   { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand GenerateCommand  { get; }

    public MainViewModel()
        : this(new StilParser(), new SignalConfigService()) { }

    public MainViewModel(IStilParser parser, ISignalConfigService csvService)
    {
        _parser = parser;
        _csvService = csvService;

        LoadStilCommand  = new RelayCommand(DoLoadStil);
        LoadCsvCommand   = new RelayCommand(DoLoadCsv);
        ExportCsvCommand = new RelayCommand(DoExportCsv, _ => Signals.Count > 0);
        GenerateCommand  = new RelayCommand(DoGenerate,
            _ => Signals.Count > 0 && _parseResult != null);

        AppendStatus("Ready.");
    }

    // ?? Load STIL ??????????????????????????????????????????????????
    private void DoLoadStil(object? _)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "STIL Files (*.stil)|*.stil|All Files (*.*)|*.*",
            Title = "Open STIL File"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            StatusText = "Parsing STIL…";
            // Load only the configuration (signals, groups, timing) for the UI.
            // The large Pattern block is expanded later, at generation time, so
            // opening a big STIL file stays fast.
            _stilFilePath = dlg.FileName;
            _parseResult = _parser.Parse(dlg.FileName, expandPattern: false);

            Signals.Clear();
            foreach (var s in _parseResult.Signals)
                Signals.Add(s);

            StatusText = $"Parsed {_parseResult.Signals.Count} signals, " +
                         $"{_parseResult.Timing.TimeSets.Count} timesets. " +
                         $"Pattern loaded on generate.";
        }
        catch (Exception ex)
        {
            StatusText = $"Parse error: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"Error parsing STIL file:\n{ex.Message}",
                "Parse Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    // ?? Load CSV (000 / SIG) ???????????????????????????????????????
    private void DoLoadCsv(object? _)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Signal files (000;*.sig;*.csv)|000;*.sig;*.csv|All Files (*.*)|*.*",
            Title = "Import Signal Configuration"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var mapping = _csvService.ImportCsv(dlg.FileName);

            if (Signals.Count > 0)
            {
                // A STIL file is already loaded: use the imported signal file to
                // filter and rename the existing signals. The kept signals are
                // those marked Enabled (000/SIG "Remove?"=false, or native Enabled).
                var current = Signals.ToList();
                _csvService.ApplyMapping(current, mapping);

                Signals.Clear();
                // Kept signals first, in 000/SIG file order; excluded signals after.
                foreach (var s in current
                             .OrderByDescending(s => s.Enabled)
                             .ThenBy(s => s.MappingOrder))
                    Signals.Add(s);

                int kept = current.Count(s => s.Enabled);
                StatusText = $"Applied mapping from {System.IO.Path.GetFileName(dlg.FileName)}: " +
                             $"{kept} of {current.Count} signals kept for conversion.";
            }
            else
            {
                // No STIL loaded yet: load the mapping rows directly; their
                // Enabled flag marks which signals are kept for conversion.
                Signals.Clear();
                foreach (var s in mapping)
                    Signals.Add(s);
                int kept = mapping.Count(s => s.Enabled);
                StatusText = $"Imported {mapping.Count} signals " +
                             $"({kept} kept for conversion) from {System.IO.Path.GetFileName(dlg.FileName)}.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Import error: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"Error importing file:\n{ex.Message}",
                "Import Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    // ?? Export CSV ??????????????????????????????????????????????????
    private void DoExportCsv(object? _)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|All Files (*.*)|*.*",
            Title = "Export Signal Configuration",
            FileName = "signals"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _csvService.ExportCsv(dlg.FileName, Signals);
            StatusText = $"Exported {Signals.Count} signals.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export error: {ex.Message}";
        }
    }

    // ?? Generate output files ??????????????????????????????????????
    private async void DoGenerate(object? _)
    {
        if (_parseResult == null)
        {
            System.Windows.MessageBox.Show(
                "Please load a STIL file first.", "Info",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select output folder"
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        string dir = dlg.SelectedPath;
        var enabled = Signals.Where(s => s.Enabled).ToList();

        if (enabled.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No enabled signals. Enable at least one signal.",
                "Warning", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        StatusText = "Generating…";
        string baseName = System.IO.Path.GetFileNameWithoutExtension(
            _parseResult.Pattern.PatternName == "_pattern_"
                ? "output"
                : _parseResult.Pattern.PatternName);

        // Created on the UI thread, so Report callbacks marshal back to it and can
        // safely update the bound progress properties.
        var progress = new Progress<(int current, int total)>(p =>
        {
            ProgressMaximum = p.total;
            ProgressValue = p.current;
            ProgressText = $"line {p.current:N0}/{p.total:N0}";
        });

        try
        {
            ProgressText = "Preparing…";
            ProgressValue = 0;
            ProgressVisible = true;

            var result = _parseResult;   // non-null (guarded above)
            var stilPath = _stilFilePath;

            await Task.Run(() =>
            {
                // Pinmap and timing only need the lightweight configuration.
                new PinmapGenerator().Generate(
                    System.IO.Path.Combine(dir, baseName + ".pinmap"), enabled);

                new TimingGenerator(result.SignalGroups).Generate(
                    System.IO.Path.Combine(dir, baseName + ".digitiming"),
                    result.Timing, enabled);

                // The pattern can contain millions of vectors. Stream them
                // straight to disk so memory stays flat instead of expanding the
                // whole pattern into a List first. Signal edits made in the UI are
                // preserved because generation uses the UI's `enabled` list.
                if (stilPath != null)
                {
                    new DigiPatGenerator().GenerateStreaming(
                        System.IO.Path.Combine(dir, baseName + ".digipatsrc"),
                        result.Pattern, enabled,
                        sink =>
                        {
                            var streamResult = _parser.ParseStreaming(stilPath, sink);
                            // Propagate the freshly-read metadata (name / complete).
                            result.Pattern.IsComplete = streamResult.Pattern.IsComplete;
                            return streamResult.Pattern.IsComplete;
                        },
                        stilPath, progress);
                }
                else
                {
                    // No source path (e.g. already-expanded pattern): write directly.
                    new DigiPatGenerator().Generate(
                        System.IO.Path.Combine(dir, baseName + ".digipatsrc"),
                        result.Pattern, enabled, null, progress);
                }
            });

            ProgressVisible = false;
            StatusText = $"Done – files saved to {dir}";
            System.Windows.MessageBox.Show(
                "Generation complete!", "Success",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ProgressVisible = false;
            StatusText = $"Generation error: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"Error during generation:\n{ex.Message}\n{ex.StackTrace}",
                "Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    // ?? INotifyPropertyChanged ?????????????????????????????????????
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
