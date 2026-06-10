using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PatternConversionTool.Models;

public class Signal : INotifyPropertyChanged
{
    private string _name = "";
    private string _direction = "";
    private bool _enabled = true;
    private bool _remote;
    private string _group = "";
    private string _source = "";
    private string _originalStilName = "";
    private int _mappingOrder = int.MaxValue;

    /// <summary>Display / alias name (editable).</summary>
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>In / Out / InOut – comes from STIL, read-only in UI.</summary>
    public string Direction
    {
        get => _direction;
        set { _direction = value; OnPropertyChanged(); }
    }

    /// <summary>Whether this signal is included in output files.</summary>
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnPropertyChanged(); }
    }

    /// <summary>Whether this signal is flagged as "remote" (Remove in 000 file).</summary>
    public bool Remote
    {
        get => _remote;
        set { _remote = value; OnPropertyChanged(); }
    }

    /// <summary>User-defined grouping label.</summary>
    public string Group
    {
        get => _group;
        set { _group = value; OnPropertyChanged(); }
    }

    /// <summary>Where this signal came from: STIL, CSV, USER.</summary>
    public string Source
    {
        get => _source;
        set { _source = value; OnPropertyChanged(); }
    }

    /// <summary>The original signal name inside the STIL file (used for mapping vectors).</summary>
    public string OriginalStilName
    {
        get => _originalStilName;
        set { _originalStilName = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Position of this signal in the 000/SIG mapping file. Used to order the
    /// kept signals in the generated output. <see cref="int.MaxValue"/> when the
    /// signal is not referenced by a mapping file.
    /// </summary>
    public int MappingOrder
    {
        get => _mappingOrder;
        set { _mappingOrder = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
