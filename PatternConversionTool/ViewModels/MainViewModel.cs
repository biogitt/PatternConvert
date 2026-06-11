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
    private string _statusText = "Ready.";

    private bool _progressVisible;
    private double _progressValue;
    private double _progressMaximum = 1;
    private string _progressText = "";

    public ObservableCollection<Signal> Signals { get; } = new();

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
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
                // A STIL file is already loaded: use the 000/SIG file to filter
                // and rename the existing signals. The "Remove?" column decides
                // which signals are kept (Enabled) for conversion.
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
                // No STIL loaded yet: load the mapping rows directly, keeping only
                // the signals flagged for conversion (Remove? == false).
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
            Filter = "Signal files (*.sig)|*.sig|CSV (*.csv)|*.csv|All Files (*.*)|*.*",
            Title = "Export Signal Configuration",
            FileName = "000"
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

            await Task.Run(() =>
            {
                // The UI load skipped the Pattern block for speed; expand it now
                // (on this background thread) so the vectors are available. Signal
                // edits made in the UI are preserved because generation uses the
                // UI's `enabled` list for pins and only the pattern vectors here.
                if (_parseResult.Pattern.Vectors.Count == 0 && _stilFilePath != null)
                    _parseResult.Pattern =
                        _parser.Parse(_stilFilePath, expandPattern: true).Pattern;

                new PinmapGenerator().Generate(
                    System.IO.Path.Combine(dir, baseName + ".pinmap"), enabled);

                new TimingGenerator(_parseResult.SignalGroups).Generate(
                    System.IO.Path.Combine(dir, baseName + ".digitiming"),
                    _parseResult.Timing, enabled);

                new DigiPatGenerator().Generate(
                    System.IO.Path.Combine(dir, baseName + ".digipatsrc"),
                    _parseResult.Pattern, enabled, _stilFilePath, progress);
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
