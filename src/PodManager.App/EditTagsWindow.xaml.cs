using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PodManager.Core;

namespace PodManager.App;

/// <summary>
/// "Get Info" for one or many songs. Fields that differ across the selection start blank with a
/// "Mixed" hint and are only written if you type something; nothing you don't touch changes.
/// </summary>
public partial class EditTagsWindow : Window
{
    private const string MixedHint = "Mixed (leave blank to keep)";
    private readonly List<Track> _tracks;
    private readonly Dictionary<TextBox, string> _initial = [];
    private byte[]? _newArt;
    private bool _removeArt;

    public TagEdit? Result { get; private set; }

    public EditTagsWindow(IReadOnlyList<Track> tracks)
    {
        InitializeComponent();
        _tracks = tracks.ToList();
        var single = _tracks.Count == 1;
        var first = _tracks[0];

        HeaderTitle.Text = single ? first.DisplayTitle : $"{_tracks.Count} songs";
        HeaderSub.Text = single
            ? $"{first.DisplayArtist} — {first.DisplayAlbum}   ·   {first.QualityText}   ·   {Format.Duration(first.Duration)}"
            : Common(t => t.DisplayAlbum) is { Length: > 0 } album ? $"{Common(t => t.DisplayAlbumArtist)} — {album}".Trim(' ', '—') : "Several albums";
        FooterNote.Text = single
            ? "Changes are written into the file on the iPod."
            : "Only fields you change are written. Fields marked Mixed keep each song's own value unless you type a new one.";

        Fill(TitleBox, t => t.Title);
        Fill(ArtistBox, t => t.Artist);
        Fill(AlbumArtistBox, t => t.AlbumArtist);
        Fill(AlbumBox, t => t.Album);
        Fill(GenreBox, t => t.Genre);
        Fill(YearBox, t => t.Year == 0 ? "" : t.Year.ToString());
        Fill(TrackBox, t => t.TrackNumber == 0 ? "" : t.TrackNumber.ToString());
        Fill(DiscBox, t => t.DiscNumber == 0 ? "" : t.DiscNumber.ToString());
        if (!single)
        {
            TitleBox.IsEnabled = false;
            TitleBox.Tag = "Can't rename several songs at once";
            TitleBox.Text = "";
            _initial[TitleBox] = "";
        }

        Loaded += async (_, _) =>
        {
            (single ? TitleBox : AlbumBox).Focus();
            var sameAlbum = Common(t => t.DisplayAlbum + "\u001f" + t.DisplayAlbumArtist) is { Length: > 0 };
            if (single || sameAlbum)
            {
                var art = await ArtLoader.LoadAsync(_tracks.FirstOrDefault(t => t.HasEmbeddedArt) ?? first, 330);
                if (_newArt is null && !_removeArt) ShowArt(art);
                ArtNote.Text = _tracks.Any(t => t.HasEmbeddedArt) ? "" : art is null ? "No artwork." : "From a cover image in the album folder.";
            }
            else
            {
                ArtNote.Text = "These songs are from different albums. Choosing artwork sets it on all of them.";
            }
        };
    }

    /// <summary>The value if every selected track agrees on it, otherwise "".</summary>
    private string Common(Func<Track, string> get)
    {
        var values = _tracks.Select(get).Distinct(StringComparer.Ordinal).Take(2).ToList();
        return values.Count == 1 ? values[0] : "";
    }

    private void Fill(TextBox box, Func<Track, string> get)
    {
        var mixed = _tracks.Select(get).Distinct(StringComparer.Ordinal).Skip(1).Any();
        box.Text = mixed ? "" : get(_tracks[0]);
        box.Tag = mixed ? MixedHint : "";
        _initial[box] = box.Text;
    }

    private void ShowArt(System.Windows.Media.Imaging.BitmapSource? art)
    {
        ArtImage.Source = art;
        ArtPlaceholder.Visibility = art is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChooseArt_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose artwork", Filter = "Images|*.jpg;*.jpeg;*.png|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var bytes = File.ReadAllBytes(dlg.FileName);
        var preview = ArtLoader.Decode(bytes, 330);
        if (preview is null)
        {
            MessageBox.Show(this, "That file isn't an image PodManager can read. Use a JPEG or PNG.", "Get Info",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (bytes.Length > 1_500_000)
            ArtNote.Text = $"This image is {Format.Bytes(bytes.Length)}. Smaller images (under about 500 KB) keep the album folders small; the iPod gets its own scaled copy either way.";
        else
            ArtNote.Text = $"New artwork ({preview.PixelWidth}×{preview.PixelHeight}).";
        _newArt = bytes;
        _removeArt = false;
        ShowArt(preview);
    }

    private void RemoveArt_Click(object sender, RoutedEventArgs e)
    {
        _newArt = null;
        _removeArt = true;
        ShowArt(null);
        ArtNote.Text = "Embedded artwork will be removed.";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string? Text(TextBox box) => box.IsEnabled && box.Text != _initial[box] ? box.Text.Trim() : null;

        uint? Number(TextBox box, string label)
        {
            var text = Text(box);
            if (text is null) return null;
            if (text.Length == 0) return 0;
            if (uint.TryParse(text, out var n)) return n;
            throw new FormatException($"{label} must be a whole number.");
        }

        try
        {
            Result = new TagEdit
            {
                Title = Text(TitleBox),
                Artist = Text(ArtistBox),
                AlbumArtist = Text(AlbumArtistBox),
                Album = Text(AlbumBox),
                Genre = Text(GenreBox),
                Year = Number(YearBox, "Year"),
                TrackNumber = Number(TrackBox, "Track"),
                DiscNumber = Number(DiscBox, "Disc"),
                Artwork = _newArt,
                RemoveArtwork = _removeArt,
            };
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, ex.Message, "Get Info", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
