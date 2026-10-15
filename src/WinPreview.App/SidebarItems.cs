using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using WinPreview.Core.Pdf;

namespace WinPreview;

public sealed class ThumbItem : INotifyPropertyChanged
{
    private ImageSource? _image;
    private string _label = "";

    public int Index { get; set; }
    public double DisplayWidth { get; set; }
    public double DisplayHeight { get; set; }
    public string Label { get => _label; set => Set(ref _label, value); }
    public ImageSource? Image { get => _image; set => Set(ref _image, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record NoteItem(int Page, Guid Id, string Header, string Text, Brush Swatch);

public sealed record ResultItem(int Index, PdfSearchHit Hit, string Header, string Context);

public sealed record TocEntry(string Title, int Page)
{
    public override string ToString() => Title;
}
