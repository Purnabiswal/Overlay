using System.Drawing.Drawing2D;

namespace MyOverlay;

/// <summary>Ephemeral screenshot shelf. Images live only in this control until the overlay closes.</summary>
public sealed class ScreenshotSidebar : UserControl
{
    private readonly FlowLayoutPanel _items;
    private readonly Label _status;
    private readonly List<ScreenshotItem> _screenshots = new();
    private ScreenshotItem? _selected;
    private int _nextNumber = 1;

    public event EventHandler? CaptureRequested;
    public event EventHandler? ConfigureTargetRequested;
    public event EventHandler? ClearTargetRequested;
    public event EventHandler? CollapseRequested;
    public event Action<ScreenshotItem?>? SelectionChanged;
    public event Action<ScreenshotItem>? InsertSelectedRequested;
    public IReadOnlyList<ScreenshotItem> Screenshots => _screenshots;
    public ScreenshotItem? SelectedScreenshot => _selected;

    public ScreenshotSidebar()
    {
        Dock = DockStyle.Right;
        Width = 260;
        MinimumSize = new Size(180, 0);
        BackColor = Color.FromArgb(22, 24, 36);
        Padding = new Padding(8);

        var header = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.FromArgb(35, 33, 59) };
        var title = new Label { Text = "✦ Screenshots", AutoSize = true, Location = new Point(8, 12), Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.FromArgb(196, 181, 253) };
        var collapse = MakeButton("❯", "Collapse sidebar", new Size(28, 28));
        collapse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        collapse.Location = new Point(header.Width - 34, 7);
        collapse.Click += (_, _) => CollapseRequested?.Invoke(this, EventArgs.Empty);
        header.Resize += (_, _) => collapse.Left = header.ClientSize.Width - collapse.Width - 6;
        header.Controls.Add(title); header.Controls.Add(collapse);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(0, 6, 0, 2), WrapContents = true, BackColor = BackColor };
        var capture = MakeButton("✂ Capture", "Capture the current screen and prepare it for the chat", new Size(112, 28));
        capture.Click += (_, _) => CaptureRequested?.Invoke(this, EventArgs.Empty);
        var configure = MakeButton("◎ Set target", "Click the ChatGPT composer after enabling this", new Size(112, 28));
        configure.Click += (_, _) => ConfigureTargetRequested?.Invoke(this, EventArgs.Empty);
        var clear = MakeButton("Clear target", "Forget the saved composer position", new Size(112, 24));
        clear.Click += (_, _) => ClearTargetRequested?.Invoke(this, EventArgs.Empty);
        var insert = MakeButton("Insert selected", "Paste the selected screenshot into the current composer", new Size(112, 24));
        insert.Click += (_, _) =>
        {
            if (_selected != null) InsertSelectedRequested?.Invoke(_selected);
            else SetStatus("Select a screenshot, then choose Insert selected.");
        };
        actions.Controls.AddRange([capture, configure, clear, insert]);

        _status = new Label { Dock = DockStyle.Top, Height = 37, Padding = new Padding(4, 3, 4, 3), ForeColor = Color.FromArgb(165, 180, 252), Font = new Font("Segoe UI", 8.5f), Text = "Target: not set", AutoEllipsis = true };
        _items = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.FromArgb(18, 19, 29), Padding = new Padding(6) };
        Controls.Add(_items); Controls.Add(_status); Controls.Add(actions); Controls.Add(header);
    }

    public void SetStatus(string text) => _status.Text = text;

    public void AddScreenshot(Bitmap bitmap)
    {
        var item = new ScreenshotItem(_nextNumber++, bitmap);
        _screenshots.Insert(0, item); // visual ordering: newest first; capture number remains immutable.
        _selected = item;
        RebuildCards();
    }

    private void RebuildCards()
    {
        _items.SuspendLayout();
        foreach (Control existing in _items.Controls) existing.Dispose();
        _items.Controls.Clear();
        foreach (var item in _screenshots) _items.Controls.Add(CreateCard(item));
        _items.ResumeLayout();
    }

    private Control CreateCard(ScreenshotItem item)
    {
        var card = new Panel { Width = Math.Max(145, _items.ClientSize.Width - 24), Height = 138, Margin = new Padding(0, 0, 0, 8), BackColor = ReferenceEquals(item, _selected) ? Color.FromArgb(91, 78, 164) : Color.FromArgb(45, 42, 72), Cursor = Cursors.Hand, Tag = item, AllowDrop = true };
        var picture = new PictureBox { Dock = DockStyle.Fill, Image = new Bitmap(item.Image), SizeMode = PictureBoxSizeMode.Zoom, Padding = new Padding(3) };
        card.Disposed += (_, _) => picture.Image?.Dispose();
        var number = new Label { Text = item.Number.ToString(), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(26, 24), Location = new Point(5, 5), BackColor = Color.FromArgb(236, 72, 153), ForeColor = Color.White, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        var hint = new Label { Text = "Drag to reorder", AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, ForeColor = Color.FromArgb(221, 214, 254), BackColor = Color.FromArgb(45, 42, 72), Location = new Point(Math.Max(34, card.Width - 108), card.Height - 21), Font = new Font("Segoe UI", 7.5f) };
        card.Controls.Add(picture); card.Controls.Add(number); card.Controls.Add(hint);
        Point dragStart = Point.Empty;
        bool trackingDrag = false;
        void BeginPointer(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragStart = e.Location; trackingDrag = true; } }
        void MovePointer(MouseEventArgs e)
        {
            if (!trackingDrag || (e.Button & MouseButtons.Left) == 0) return;
            Size threshold = SystemInformation.DragSize;
            Rectangle dragBounds = new(dragStart.X - threshold.Width / 2, dragStart.Y - threshold.Height / 2, threshold.Width, threshold.Height);
            if (!dragBounds.Contains(e.Location)) { trackingDrag = false; card.DoDragDrop(item, DragDropEffects.Move); }
        }
        void SelectItem(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !trackingDrag) return;
            trackingDrag = false;
            if (!ReferenceEquals(_selected, item)) { _selected = item; SelectionChanged?.Invoke(_selected); RebuildCards(); }
        }
        card.MouseDown += (_, e) => BeginPointer(e);
        card.MouseMove += (_, e) => MovePointer(e);
        card.MouseUp += (_, e) => SelectItem(e);
        picture.MouseDown += (_, e) => BeginPointer(e);
        picture.MouseMove += (_, e) => MovePointer(e);
        picture.MouseUp += (_, e) => SelectItem(e);
        card.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(typeof(ScreenshotItem)) == true ? DragDropEffects.Move : DragDropEffects.None;
        card.DragDrop += (_, e) => { if (e.Data?.GetData(typeof(ScreenshotItem)) is ScreenshotItem source && source != item) { _screenshots.Remove(source); _screenshots.Insert(_screenshots.IndexOf(item), source); RebuildCards(); } };
        card.DoubleClick += (_, _) => ShowPreview(item);
        picture.DoubleClick += (_, _) => ShowPreview(item);
        return card;
    }

    private void ShowPreview(ScreenshotItem item)
    {
        var preview = new Form { Text = $"Screenshot {item.Number}", StartPosition = FormStartPosition.CenterParent, Size = new Size(900, 650), BackColor = Color.FromArgb(20, 20, 28) };
        preview.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = item.Image, SizeMode = PictureBoxSizeMode.Zoom });
        preview.Show(this);
    }

    private static Button MakeButton(string text, string tooltip, Size size)
    {
        var button = new Button { Text = text, Size = size, Margin = new Padding(2), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(79, 70, 229), ForeColor = Color.White, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Color.FromArgb(124, 58, 237);
        new ToolTip().SetToolTip(button, tooltip); return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var shot in _screenshots) shot.Image.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class ScreenshotItem
{
    public ScreenshotItem(int number, Bitmap image) { Number = number; Image = image; }
    public int Number { get; }
    public Bitmap Image { get; }
}
