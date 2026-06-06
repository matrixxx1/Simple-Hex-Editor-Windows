using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace SimpleHexEditor;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<HexLine> _lines = [];
    private readonly HashSet<int> _bookmarkOffsets = [];
    private readonly List<int> _matchOffsets = [];
    private byte[] _data = [];
    private byte[] _baselineData = [];
    private string _currentFilePath = string.Empty;
    private int _matchIndex;
    private int _selectedOffset = -1;
    private readonly Stack<ByteEdit> _undoStack = [];
    private readonly Stack<ByteEdit> _redoStack = [];
    private readonly HashSet<int> _modifiedOffsets = [];

    public MainWindow()
    {
        InitializeComponent();
        ByteGrid.ItemsSource = _lines;
        RefreshUiState();
        UpdateUndoRedoStatus();
        SetStatus("Load a file to begin.");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            OnUndoEdit(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.O)
        {
            OnOpenSampleFile(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            OnRedoEdit(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog() != true)
            return;

        LoadFile(dlg.FileName);
    }

    private void OnReloadFile(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            SetStatus("No file loaded.");
            return;
        }

        LoadFile(_currentFilePath);
        SetStatus("Reloaded file.");
    }

    private void OnSaveFile(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            SetStatus("No file loaded.");
            return;
        }

        var dlg = new SaveFileDialog();
        dlg.FileName = Path.GetFileName(_currentFilePath);
        dlg.InitialDirectory = Path.GetDirectoryName(_currentFilePath);
        dlg.Filter = "All files (*.*)|*.*";

        if (dlg.ShowDialog() != true)
            return;

        File.WriteAllBytes(dlg.FileName, _data);
        _baselineData = (byte[])_data.Clone();
        _modifiedOffsets.Clear();
        RebuildRows();
        UpdateUndoRedoStatus();
        SetStatus($"Saved {dlg.FileName}");
    }

    private void OnExportHexDump(object sender, RoutedEventArgs e)
    {
        if (_data.Length == 0)
        {
            SetStatus("No file loaded.");
            return;
        }

        var dlg = new SaveFileDialog();
        dlg.FileName = "hex-dump.txt";
        dlg.Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*";

        if (dlg.ShowDialog() != true)
            return;

        var lines = new List<string>(_lines.Count + 1) { "Offset,Hex,ASCII" };
        foreach (var line in _lines)
        {
            lines.Add($"{line.OffsetText},{line.HexText},{line.AsciiText}");
        }

        File.WriteAllLines(dlg.FileName, lines);
        SetStatus($"Exported hex dump: {dlg.FileName}");
    }

    private void OnFindBytes(object sender, RoutedEventArgs e)
    {
        if (_data.Length == 0)
        {
            SetStatus("No file loaded.");
            return;
        }

        if (!TryParseHexBytes(SearchText.Text, out var queryBytes))
        {
            SetStatus("Search input must be hex bytes, e.g. DE AD BE EF or DEADBEEF.");
            return;
        }

        _matchOffsets.Clear();
        _matchIndex = 0;

        if (queryBytes.Length == 0)
        {
            SetStatus("Search text is empty.");
            return;
        }

        for (int i = 0; i <= _data.Length - queryBytes.Length; i++)
        {
            bool isMatch = true;
            for (int j = 0; j < queryBytes.Length; j++)
            {
                if (_data[i + j] != queryBytes[j])
                {
                    isMatch = false;
                    break;
                }
            }

            if (isMatch)
            {
                _matchOffsets.Add(i);
            }
        }

        foreach (var line in _lines)
            line.IsMatch = false;

        foreach (var match in _matchOffsets)
        {
            int lineIndex = match / 16;
            if (lineIndex < _lines.Count)
                _lines[lineIndex].IsMatch = true;
        }

        if (_matchOffsets.Count > 0)
        {
            ByteGrid.SelectedItem = _lines[_matchOffsets[0] / 16];
            ByteGrid.ScrollIntoView(_lines[_matchOffsets[0] / 16]);
            MatchInfo.Text = $"{_matchOffsets.Count} match(es), showing {0 + 1}.";
            SetStatus($"Found {_matchOffsets.Count} match(es).");
        }
        else
        {
            MatchInfo.Text = "No matches";
            SetStatus("No matches found.");
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e)
    {
        if (_matchOffsets.Count == 0)
        {
            SetStatus("Run Find first.");
            return;
        }

        _matchIndex = (_matchIndex + 1) % _matchOffsets.Count;
        int matchOffset = _matchOffsets[_matchIndex];
        ByteGrid.SelectedItem = _lines[matchOffset / 16];
        ByteGrid.ScrollIntoView(_lines[matchOffset / 16]);
        MatchInfo.Text = $"{_matchOffsets.Count} match(es), showing {_matchIndex + 1}.";
        SetStatus($"Match {_matchIndex + 1} of {_matchOffsets.Count}.");
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchText.Text = string.Empty;
        _matchOffsets.Clear();
        _matchIndex = 0;
        foreach (var line in _lines)
            line.IsMatch = false;
        MatchInfo.Text = "No matches";
        SetStatus("Search cleared.");
    }

    private void OnGoToOffset(object sender, RoutedEventArgs e)
    {
        if (_data.Length == 0)
        {
            SetStatus("No file loaded.");
            return;
        }

        if (!TryParseOffset(GotoOffsetText.Text, out var offset))
        {
            SetStatus("Offset must be decimal (42) or hex (0x2A or 2A).");
            return;
        }

        offset = Math.Min(Math.Max(0, offset), Math.Max(0, _data.Length - 1));
        int line = offset / 16;
        ByteGrid.SelectedItem = _lines[line];
        ByteGrid.ScrollIntoView(_lines[line]);
        SetStatus($"Jumped to offset {offset} (0x{offset:X}).");
    }

    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        if (_selectedOffset < 0)
        {
            SetStatus("Select a row first.");
            return;
        }

        Clipboard.SetText(_lines[_selectedOffset / 16].HexText);
        SetStatus("Copied row hex data.");
    }

    private void OnCopySelection(object sender, RoutedEventArgs e)
    {
        if (ByteGrid.SelectedItems.Count == 0)
        {
            SetStatus("Select a row first.");
            return;
        }

        var line = ByteGrid.SelectedItem as HexLine;
        if (line is null) return;

        Clipboard.SetText($"{line.OffsetText}\t{line.HexText}\t{line.AsciiText}");
        SetStatus("Copied selected row with offset and ASCII.");
    }

    private void OnApplyByteEdit(object sender, RoutedEventArgs e)
    {
        if (_data.Length == 0)
        {
            SetStatus("No file loaded.");
            return;
        }

        if (!TryParseOffset(EditOffsetText.Text, out int offset) || offset < 0 || offset >= _data.Length)
        {
            SetStatus("Edit offset invalid.");
            return;
        }

        if (!TryParseHexByte(EditHexValueText.Text, out byte value))
        {
            SetStatus("Edit value must be a byte in hex (0x00 - 0xFF).");
            return;
        }

        var oldValue = _data[offset];
        _data[offset] = value;
        SetModified(offset, oldValue, value);
        _undoStack.Push(new ByteEdit(offset, oldValue, value));
        _redoStack.Clear();
        RefreshSingleLine(offset);
        UpdateUndoRedoStatus();
        SelectionText.Text = $"Edited byte at offset {offset} (0x{offset:X}) to 0x{value:X2}.";
        SetStatus("Byte edit applied.");
    }

    private void OnUndoEdit(object sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0)
        {
            SetStatus("Nothing to undo.");
            return;
        }

        var edit = _undoStack.Pop();
        var current = _data[edit.Offset];
        _data[edit.Offset] = edit.OldValue;
        SetModified(edit.Offset, current, edit.OldValue);
        _redoStack.Push(new ByteEdit(edit.Offset, current, edit.OldValue));
        RefreshSingleLine(edit.Offset);
        UpdateUndoRedoStatus();
        SelectionText.Text = $"Undo: restored offset 0x{edit.Offset:X} to 0x{edit.OldValue:X2}.";
        SetStatus("Undo completed.");
    }

    private void OnRedoEdit(object sender, RoutedEventArgs e)
    {
        if (_redoStack.Count == 0)
        {
            SetStatus("Nothing to redo.");
            return;
        }

        var edit = _redoStack.Pop();
        var current = _data[edit.Offset];
        _data[edit.Offset] = edit.NewValue;
        SetModified(edit.Offset, current, edit.NewValue);
        _undoStack.Push(new ByteEdit(edit.Offset, current, edit.NewValue));
        RefreshSingleLine(edit.Offset);
        UpdateUndoRedoStatus();
        SelectionText.Text = $"Redo: changed offset 0x{edit.Offset:X} to 0x{edit.NewValue:X2}.";
        SetStatus("Redo completed.");
    }

    private void OnOpenSampleFile(object sender, RoutedEventArgs e)
    {
        var temp = Path.Combine(Path.GetTempPath(), "SimpleHexEditor_Sample.bin");
        byte[] sample = [
            0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, // DOS header start
            0x46, 0x4F, 0x4F, 0x20, 0x48, 0x45, 0x58, 0x21,
            0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80,
            0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04,
            0x53, 0x69, 0x6D, 0x70, 0x6C, 0x65, 0x20, 0x48,
            0x65, 0x78, 0x20, 0x45, 0x64, 0x69, 0x74, 0x6F,
            0x72, 0x0A, 0x53, 0x61, 0x6D, 0x70, 0x6C, 0x65, 
            0x20, 0x4D, 0x65, 0x6D, 0x6F, 0x72, 0x79, 0x0A
        ];
        File.WriteAllBytes(temp, sample);
        LoadFile(temp);
        SetStatus("Loaded sample file.");
    }

    private void OnDiffModeToggled(object sender, RoutedEventArgs e)
    {
        RebuildRows();
    }

    private void OnAddBookmark(object sender, RoutedEventArgs e)
    {
        if (!TryParseOffset(BookmarkText.Text, out int offset) || offset < 0 || offset >= _data.Length)
        {
            SetStatus("Bookmark offset invalid.");
            return;
        }

        if (_bookmarkOffsets.Add(offset))
        {
            _lines[offset / 16].IsBookmarked = true;
            RefreshBookmarks();
            SetStatus($"Bookmark added: 0x{offset:X}");
        }
        else
        {
            SetStatus("Bookmark already exists.");
        }
    }

    private void OnRemoveBookmark(object sender, RoutedEventArgs e)
    {
        if (!TryParseOffset(BookmarkText.Text, out int offset) || offset < 0 || offset >= _data.Length)
        {
            SetStatus("Bookmark offset invalid.");
            return;
        }

        if (_bookmarkOffsets.Remove(offset))
        {
            if (!_bookmarkOffsets.Any(x => x / 16 == offset / 16))
            {
                _lines[offset / 16].IsBookmarked = false;
            }

            RefreshBookmarks();
            SetStatus($"Bookmark removed: 0x{offset:X}");
        }
        else
        {
            SetStatus("Bookmark not found.");
        }
    }

    private void OnGotoBookmark(object sender, RoutedEventArgs e)
    {
        if (BookmarksList.SelectedItem is null)
        {
            SetStatus("Pick a bookmark from the list.");
            return;
        }

        var text = BookmarksList.SelectedItem.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var start = text.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            SetStatus("Could not parse selected bookmark.");
            return;
        }

        var hex = text[(start + 2)..];
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed))
            return;

        GotoOffsetText.Text = parsed.ToString(CultureInfo.InvariantCulture);
        OnGoToOffset(sender, e);
    }

    private void OnComputeChecksums(object sender, RoutedEventArgs e)
    {
        if (_data.Length == 0)
        {
            SetStatus("No file loaded.");
            return;
        }

        var crc32 = ComputeCrc32(_data);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(_data);
        var shaHex = Convert.ToHexString(hash);
        SelectionText.Text = $"CRC-32: 0x{crc32:X8}    SHA-256: {shaHex}";
        SetStatus("Checksums computed.");
    }

    private void OnClearWorkspace(object sender, RoutedEventArgs e)
    {
        _data = [];
        _currentFilePath = string.Empty;
        _baselineData = [];
        _modifiedOffsets.Clear();
        _bookmarkOffsets.Clear();
        _matchOffsets.Clear();
        _matchIndex = 0;
        _undoStack.Clear();
        _redoStack.Clear();
        _lines.Clear();
        RefreshBookmarks();
        MatchInfo.Text = "No matches";
        FilePathText.Text = "No file loaded.";
        FileMetricsText.Text = "—";
        SelectionText.Text = "No selection.";
        UpdateUndoRedoStatus();
        SetStatus("Workspace cleared.");
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ByteGrid.SelectedItem is not HexLine line)
            return;

        int lineIndex = _lines.IndexOf(line);
        _selectedOffset = lineIndex * 16;
        int displayedCount = Math.Min(16, Math.Max(0, _data.Length - _selectedOffset));
        SelectionText.Text = $"Row start: 0x{_selectedOffset:X} ({_selectedOffset}), selected bytes: {displayedCount}";
        EditOffsetText.Text = _selectedOffset.ToString(CultureInfo.InvariantCulture);
    }

    private void LoadFile(string path)
    {
        _currentFilePath = path;
        _data = File.ReadAllBytes(path);
        _baselineData = (byte[])_data.Clone();
        _modifiedOffsets.Clear();
        _bookmarkOffsets.Clear();
        _matchOffsets.Clear();
        _matchIndex = 0;
        _undoStack.Clear();
        _redoStack.Clear();
        MatchInfo.Text = "No matches";

        BuildRows();
        FilePathText.Text = path;
        FileMetricsText.Text = $"{_data.Length:N0} bytes | MD5: {ComputeMd5(_data)} | UTF-8 starts: {_data.Take(8).TakeWhile(b => b >= 32 && b <= 126).Count()} printable";
        RefreshBookmarks();
        UpdateUndoRedoStatus();
        SetStatus($"Loaded {_data.Length:N0} bytes.");
    }

    private void BuildRows()
    {
        _lines.Clear();
        for (int i = 0; i < _data.Length; i += 16)
        {
            _lines.Add(CreateLine(i));
        }
    }

    private void RebuildRows()
    {
        int preservedSelection = _selectedOffset;
        BuildRows();
        if (preservedSelection >= 0 && preservedSelection < _data.Length)
        {
            ByteGrid.SelectedItem = _lines[preservedSelection / 16];
        }
    }

    private void SetModified(int offset, byte before, byte after)
    {
        if (offset < 0 || offset >= _data.Length || _baselineData.Length == 0)
            return;

        if (after != _baselineData[offset])
            _modifiedOffsets.Add(offset);
        else
            _modifiedOffsets.Remove(offset);

        RefreshSingleLine(offset);
    }

    private void RefreshSingleLine(int offset)
    {
        int lineIndex = offset / 16;
        if (lineIndex < 0 || lineIndex >= _lines.Count)
            return;

        _lines[lineIndex] = CreateLine(lineIndex * 16);
        ByteGrid.Items.Refresh();
    }

    private HexLine CreateLine(int startOffset)
    {
        var bytes = new byte[Math.Min(16, _data.Length - startOffset)];
        Array.Copy(_data, startOffset, bytes, 0, bytes.Length);

        var hexValues = bytes.Select(b => b.ToString("X2"));
        var displayHex = string.Join(" ", hexValues).PadRight(47); // keeps visual alignment

        var ascii = new string(bytes.Select(b => b is >= 32 and <= 126 ? (char)b : '.').ToArray());

        return new HexLine(
            startOffset,
            $"0x{startOffset:X8}",
            displayHex,
            ascii,
            _matchOffsets.Any(x => x >= startOffset && x < startOffset + 16),
            _bookmarkOffsets.Any(x => x / 16 == startOffset / 16),
            DiffModeEnabled?.IsChecked == true && _modifiedOffsets.Any(x => x >= startOffset && x < startOffset + 16));
    }

    private static string ComputeMd5(byte[] data)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(data);
        return Convert.ToHexString(hash);
    }

    private static uint ComputeCrc32(byte[] data)
    {
        const uint poly = 0xEDB88320u;
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            uint current = b;
            crc ^= current;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) == 1 ? (crc >> 1) ^ poly : crc >> 1;
            }
        }

        return ~crc;
    }

    private static bool TryParseHexBytes(string text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var cleaned = new string(text.Where(char.IsLetterOrDigit).ToArray());
        if (cleaned.Length % 2 == 1)
            return false;

        var parsed = new List<byte>(cleaned.Length / 2);
        for (int i = 0; i < cleaned.Length; i += 2)
        {
            if (!byte.TryParse(cleaned.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                return false;

            parsed.Add(b);
        }

        bytes = parsed.ToArray();
        return true;
    }

    private static bool TryParseHexByte(string text, out byte value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimStart('0', 'x', 'X');
        return byte.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseOffset(string text, out int offset)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            offset = -1;
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        if (int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset))
            return true;

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
    }

    private void RefreshBookmarks()
    {
        BookmarksList.ItemsSource = null;
        BookmarksList.ItemsSource = _bookmarkOffsets
            .OrderBy(x => x)
            .Select(x => $"0x{x:X8} ({x})");
    }

    private void RefreshUiState()
    {
        ByteGrid.ItemsSource = _lines;
    }

    private void UpdateUndoRedoStatus()
    {
        UndoRedoText.Text = $"Undo: {_undoStack.Count}, Redo: {_redoStack.Count}";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        FileMetricsText.Text = $"Status: {message}";
    }
}

public sealed record ByteEdit(int Offset, byte OldValue, byte NewValue);

public sealed class HexLine(int offset, string offsetText, string hexText, string asciiText, bool isMatch, bool isBookmarked, bool isModified)
{
    public int Offset => offset;
    public string OffsetText { get; set; } = offsetText;
    public string HexText { get; set; } = hexText;
    public string AsciiText { get; set; } = asciiText;
    public bool IsMatch { get; set; } = isMatch;
    public bool IsBookmarked { get; set; } = isBookmarked;
    public bool IsModified { get; set; } = isModified;
}
