using System.Net.Sockets;

sealed class FileManagerForm : FluentForm
{
    private readonly Tv tv;
    private readonly FileList pcList = new(), tvList = new();
    private readonly ComboBox pcDrives = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox tvVolumes = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox pcPath = new(), tvPath = new();
    private readonly Label status = new();
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private readonly Button up, refresh, makeFolder, rename, delete, copy, cut, paste, send, receive, stop;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? work;
    private List<TvVolume> volumes = [];
    private List<TvItem> tvItems = [];
    private TvClipboard? tvClip;
    private PcClipboard? pcClip;
    private string pcCurrent = "";
    private string tvCurrent = "/storage/emulated/0";
    private bool tvActive = true;
    private bool busy;
    private bool closeAfterCancel;
    private bool filling;

    internal FileManagerForm(Tv tv, bool autoLoad = true)
    {
        this.tv = tv;
        Text = Tf($"{tv.Name} · Files"); Icon = Program.AppIcon;
        ClientSize = new Size(1080, 700); MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterParent;
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No; RightToLeftLayout = IsArabic;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var toolbarRow = new RowStyle(SizeType.Absolute, 46);
        layout.RowStyles.Add(toolbarRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = T("Browse the TV's internal and external storage and this PC. Copy, move, rename or delete items, create folders, and send files in either direction."),
            Dock = DockStyle.Fill, AutoEllipsis = true
        });

        up = ActionButton("Up", Up);
        refresh = ActionButton("Refresh", RefreshActive);
        makeFolder = ActionButton("New folder", () => _ = MakeFolder());
        rename = ActionButton("Rename", () => _ = Rename());
        delete = ActionButton("Delete", () => _ = Delete());
        copy = ActionButton("Copy", () => Copy(false));
        cut = ActionButton("Cut", () => Copy(true));
        paste = ActionButton("Paste", () => _ = Paste());
        send = ActionButton("Send to TV", () => _ = SendToTv());
        receive = ActionButton("Download to PC", () => _ = DownloadToPc());
        stop = ActionButton("Stop", StopWork);
        ((FluentButton)send).Accent = true;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        toolbar.Controls.AddRange(new Control[] { up, refresh, makeFolder, rename, delete, copy, cut, paste, send, receive, stop });
        layout.Controls.Add(toolbar);
        void FitToolbar()
        {
            if (toolbar.IsDisposed || toolbar.Width <= 0) return;
            var rows = 1;
            var line = 0;
            var step = 34;
            foreach (Control control in toolbar.Controls)
            {
                var advance = control.Width + control.Margin.Horizontal;
                if (line > 0 && line + advance > toolbar.Width) { rows++; line = 0; }
                line += advance;
                step = Math.Max(step, control.Height + control.Margin.Vertical);
            }
            var scale = DeviceDpi / 96f;
            var height = rows == 1 ? (int)Math.Ceiling(46 * scale) : step * rows + (int)Math.Ceiling(8 * scale);
            if (toolbarRow.Height != height) toolbarRow.Height = height;
        }
        layout.Resize += (_, _) => FitToolbar();
        Shown += (_, _) => FitToolbar();

        var panes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(panes);
        var pcCard = Pane(T("This PC"), pcDrives, pcPath, pcList, T("This PC files"));
        pcCard.Margin = new Padding(0, 0, 8, 0);
        panes.Controls.Add(pcCard, 0, 0);
        var tvCard = Pane(tv.Name, tvVolumes, tvPath, tvList, T("TV files"));
        tvCard.Margin = new Padding(0);
        panes.Controls.Add(tvCard, 1, 0);

        layout.Controls.Add(progress);
        status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.Tag = "muted";
        layout.Controls.Add(status);

        pcList.Enter += (_, _) => SetActive(false);
        tvList.Enter += (_, _) => SetActive(true);
        pcList.DoubleClick += (_, _) => EnterPc();
        tvList.DoubleClick += (_, _) => EnterTv();
        pcList.SelectedIndexChanged += (_, _) => UpdateActions();
        tvList.SelectedIndexChanged += (_, _) => UpdateActions();
        pcList.KeyDown += ListKey; tvList.KeyDown += ListKey;
        pcDrives.SelectedIndexChanged += (_, _) => { if (!filling && pcDrives.SelectedItem is PcDrive drive) LoadPc(drive.Root); };
        tvVolumes.SelectedIndexChanged += (_, _) => { if (!filling && tvVolumes.SelectedItem is TvVolume volume) _ = LoadTvAsync(volume.Path); };

        FillDrives();
        status.Text = T("Choose a drive or TV folder to start.");
        UpdateActions();
        ActiveControl = pcList;
        PrepareLayout();
        if (autoLoad) Shown += async (_, _) => await LoadTvAsync(tvCurrent, true);
    }

    private FluentCard Pane(string title, ComboBox storages, TextBox path, FileList list, string accessible)
    {
        var card = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(12) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 24, 32, 28, 0 })
            grid.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        card.Controls.Add(grid);
        grid.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true });
        storages.Dock = DockStyle.Fill;
        storages.AccessibleName = accessible;
        grid.Controls.Add(storages);
        path.ReadOnly = true; path.BorderStyle = BorderStyle.FixedSingle; path.Dock = DockStyle.Fill;
        path.RightToLeft = RightToLeft.No; path.TabStop = false;
        path.TextAlign = HorizontalAlignment.Left;
        path.AccessibleName = accessible;
        grid.Controls.Add(path);
        list.Dock = DockStyle.Fill;
        list.AccessibleName = accessible;
        list.Columns.Add(T("Name"), 320);
        list.Columns.Add(T("Size"), 90);
        list.Columns.Add(T("Modified"), 140);
        list.Resize += (_, _) => SizeColumns(list);
        grid.Controls.Add(list);
        return card;
    }

    private static void SizeColumns(FileList list)
    {
        if (list.Columns.Count < 3 || list.IsDisposed) return;
        var scale = list.DeviceDpi / 96d;
        list.Columns[1].Width = (int)(90 * scale);
        list.Columns[2].Width = (int)(140 * scale);
        list.Columns[0].Width = Math.Max((int)(120 * scale),
            list.ClientSize.Width - list.Columns[1].Width - list.Columns[2].Width - SystemInformation.VerticalScrollBarWidth - 4);
    }

    private Button ActionButton(string text, Action action)
    {
        var button = new FluentButton { Text = T(text), AutoSize = true, MinimumSize = new Size(64, 34), Padding = new Padding(8, 0, 8, 0), Margin = new Padding(0, 0, 8, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private void SetActive(bool tvPane)
    {
        tvActive = tvPane;
        UpdateActions();
    }

    private void FillDrives()
    {
        pcDrives.Items.Clear();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        object? start = null;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                var label = drive.Name.TrimEnd('\\');
                if (!string.IsNullOrWhiteSpace(drive.VolumeLabel)) label += " (" + drive.VolumeLabel + ")";
                var entry = new PcDrive(label, root);
                pcDrives.Items.Add(entry);
                if (start is null && home.StartsWith(root, StringComparison.OrdinalIgnoreCase)) start = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (pcDrives.Items.Count == 0) { status.Text = T("No disk drives were found."); return; }
        var selected = start as PcDrive ?? pcDrives.Items[0] as PcDrive;
        if (selected is null) { status.Text = T("No disk drives were found."); return; }
        filling = true; pcDrives.SelectedItem = selected; filling = false;
        LoadPc(Directory.Exists(home) ? home : selected.Root);
    }

    private void LoadPc(string path)
    {
        if (busy) return;
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists) { status.Text = T("This PC folder is not available."); return; }
            var entries = directory.EnumerateFileSystemInfos()
                .OrderBy(info => info is not DirectoryInfo)
                .ThenBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            pcList.BeginUpdate(); pcList.Items.Clear();
            foreach (var info in entries)
            {
                var isDirectory = info is DirectoryInfo;
                pcList.Items.Add(new ListViewItem(new[]
                {
                    info.Name,
                    isDirectory ? "" : Bytes(((FileInfo)info).Length),
                    info.LastWriteTime.ToString("g")
                }) { Tag = info.FullName, ToolTipText = info.FullName });
            }
            pcList.EndUpdate();
            pcCurrent = directory.FullName;
            pcPath.Text = pcCurrent;
            status.Text = entries.Length == 0 ? T("This folder is empty.") : Tf($"{entries.Length} items");
            SizeColumns(pcList);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            status.Text = ex.Message;
        }
        UpdateActions();
    }

    private async Task LoadTvAsync(string path, bool refreshVolumes = false)
    {
        if (busy || IsDisposed) return;
        BeginWork(T("Opening the TV storage…"));
        var token = work!.Token;
        try { await ReadTvAsync(path, token, refreshVolumes); }
        catch (Exception ex) when (TransferFailure(ex)) { ShowFailure(ex, token); }
        finally { EndWork(); }
    }

    private async Task ReadTvAsync(string path, CancellationToken token, bool refreshVolumes)
    {
        if (refreshVolumes || volumes.Count == 0)
        {
            volumes = await Network.TvVolumes(tv, token);
            filling = true;
            tvVolumes.Items.Clear();
            foreach (var volume in volumes) tvVolumes.Items.Add(volume);
            filling = false;
        }
        path = Network.StorageFolder(path);
        var entries = await Network.ListTvFolder(tv, path, token);
        if (IsDisposed || token.IsCancellationRequested) return;
        tvCurrent = path;
        tvPath.Text = path;
        tvItems = entries.Select(entry => new TvItem(path + "/" + entry.Name, entry)).ToList();
        RenderTvList();
        var root = RootOf(path);
        if (root is not null && tvVolumes.SelectedItem is TvVolume current && current.Path != root)
        {
            filling = true;
            tvVolumes.SelectedItem = volumes.FirstOrDefault(volume => volume.Path == root);
            filling = false;
        }
        status.Text = entries.Count == 0 ? T("This folder is empty.") : Tf($"{entries.Count} items");
        SizeColumns(tvList);
        UpdateActions();
    }

    private void RenderTvList()
    {
        tvList.BeginUpdate(); tvList.Items.Clear();
        foreach (var item in tvItems.OrderBy(entry => !entry.Entry.IsDirectory)
                     .ThenBy(entry => entry.Entry.Name, StringComparer.CurrentCultureIgnoreCase))
            tvList.Items.Add(new ListViewItem(new[]
            {
                item.Entry.Name,
                item.Entry.IsDirectory ? "" : Bytes(item.Entry.Size),
                item.Entry.Modified.LocalDateTime.ToString("g")
            }) { Tag = item, ToolTipText = item.Path });
        tvList.EndUpdate();
    }

    private string? RootOf(string path) => volumes.Select(volume => volume.Path)
        .Where(candidate => path == candidate || path.StartsWith(candidate + "/", StringComparison.Ordinal))
        .OrderByDescending(candidate => candidate.Length).FirstOrDefault();

    private bool CanUpTv => RootOf(tvCurrent) is { } root && tvCurrent != root;
    private bool CanUpPc => Directory.GetParent(pcCurrent) is not null;

    private void UpdateActions()
    {
        var tvSelection = tvList.SelectedItems.Count;
        var pcSelection = pcList.SelectedItems.Count;
        var selected = tvActive ? tvSelection : pcSelection;
        up.Enabled = !busy && (tvActive ? CanUpTv : CanUpPc);
        refresh.Enabled = !busy;
        makeFolder.Enabled = !busy && (tvActive ? tvCurrent.Length > 0 : pcCurrent.Length > 0);
        rename.Enabled = !busy && (tvActive ? tvSelection == 1 : pcSelection == 1);
        delete.Enabled = !busy && selected > 0;
        copy.Enabled = cut.Enabled = !busy && selected > 0;
        paste.Enabled = !busy && (tvActive ? tvClip is not null : pcClip is not null);
        send.Enabled = !busy && pcSelection > 0 && pcCurrent.Length > 0;
        receive.Enabled = !busy && tvSelection > 0 && pcCurrent.Length > 0;
        stop.Enabled = busy;
        pcList.Enabled = tvList.Enabled = !busy;
        pcDrives.Enabled = tvVolumes.Enabled = !busy;
    }

    private void BeginWork(string message)
    {
        work = new CancellationTokenSource();
        busy = true;
        progress.Style = ProgressBarStyle.Marquee;
        status.Text = message;
        UpdateActions();
    }

    private void EndWork()
    {
        work?.Dispose(); work = null;
        busy = false;
        progress.Style = ProgressBarStyle.Blocks;
        progress.Value = 0;
        UpdateActions();
        if (closeAfterCancel) Close();
    }

    private void StopWork()
    {
        stop.Enabled = false;
        status.Text = T("Stopping…");
        work?.Cancel();
    }

    private static bool TransferFailure(Exception ex) =>
        ex is IOException or SocketException or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException;

    private void ShowFailure(Exception ex, CancellationToken token)
    {
        if (IsDisposed || lifetime.IsCancellationRequested) return;
        status.Text = ex is OperationCanceledException
            ? token.IsCancellationRequested ? T("Transfer cancelled. Completed files are kept.") : T("The TV did not respond in time.")
            : ex.Message;
    }

    private async Task RunTv(string message, Func<CancellationToken, Task<string>> action)
    {
        if (busy || IsDisposed) return;
        BeginWork(message);
        var token = work!.Token;
        try
        {
            var result = await action(token);
            if (IsDisposed || token.IsCancellationRequested) return;
            await ReadTvAsync(tvCurrent, token, false);
            status.Text = result;
        }
        catch (Exception ex) when (TransferFailure(ex)) { ShowFailure(ex, token); }
        finally { EndWork(); }
    }

    private void Up()
    {
        if (busy) return;
        if (tvActive)
        {
            var parent = Network.ParentTvPath(tvCurrent, volumes);
            if (parent != tvCurrent) _ = LoadTvAsync(parent);
        }
        else
        {
            var parent = Directory.GetParent(pcCurrent)?.FullName;
            if (parent is not null) LoadPc(parent);
        }
    }

    private void RefreshActive()
    {
        if (busy) return;
        if (tvActive) _ = LoadTvAsync(tvCurrent, true);
        else LoadPc(pcCurrent);
    }

    private void EnterPc()
    {
        if (pcList.SelectedItems.Count != 1) return;
        var path = (string)pcList.SelectedItems[0].Tag!;
        if (Directory.Exists(path)) LoadPc(path);
    }

    private void EnterTv()
    {
        if (tvList.SelectedItems.Count != 1) return;
        if (tvList.SelectedItems[0].Tag is TvItem { Entry.IsDirectory: true } item) _ = LoadTvAsync(item.Path);
    }

    private void ListKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter: if (tvActive) EnterTv(); else EnterPc(); e.SuppressKeyPress = true; break;
            case Keys.Back: Up(); e.SuppressKeyPress = true; break;
            case Keys.F2: _ = Rename(); e.SuppressKeyPress = true; break;
            case Keys.Delete: _ = Delete(); e.SuppressKeyPress = true; break;
            case Keys.F5: RefreshActive(); e.SuppressKeyPress = true; break;
            case Keys.C when e.Control: Copy(false); e.SuppressKeyPress = true; break;
            case Keys.X when e.Control: Copy(true); e.SuppressKeyPress = true; break;
            case Keys.V when e.Control: _ = Paste(); e.SuppressKeyPress = true; break;
        }
    }

    private async Task MakeFolder()
    {
        if (busy) return;
        if (tvActive)
        {
            var name = Prompt(T("New folder"), T("New folder name"), "", true);
            if (name is null) return;
            await RunTv(T("Creating folder…"), async token =>
            {
                var path = await Network.TvMakeFolder(tv, tvCurrent, name, token);
                return Tf($"{Path.GetFileName(path)} created.");
            });
        }
        else
        {
            var name = Prompt(T("New folder"), T("New folder name"), "", false);
            if (name is null) return;
            try
            {
                Directory.CreateDirectory(Path.Combine(pcCurrent, name));
                LoadPc(pcCurrent);
                status.Text = T("Folder created.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = ex.Message; }
        }
    }

    private async Task Rename()
    {
        if (busy) return;
        if (tvActive)
        {
            if (SelectedTvItems() is not [var item]) { status.Text = T("Nothing is selected on the TV."); return; }
            var name = Prompt(T("Rename item"), T("Name"), item.Entry.Name, true);
            if (name is null || name.Trim() == item.Entry.Name) return;
            await RunTv(T("Renaming…"), async token =>
            {
                var path = await Network.TvRenameItem(tv, item.Path, name, token);
                return Tf($"Renamed to {Path.GetFileName(path)}.");
            });
        }
        else
        {
            if (pcList.SelectedItems.Count != 1) { status.Text = T("Nothing is selected on this PC."); return; }
            var path = (string)pcList.SelectedItems[0].Tag!;
            var current = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            var name = Prompt(T("Rename item"), T("Name"), current, false);
            if (name is null || name.Trim() == current) return;
            try
            {
                var destination = Path.Combine(Path.GetDirectoryName(path)!, name);
                if (Directory.Exists(path)) Directory.Move(path, destination);
                else File.Move(path, destination);
                LoadPc(pcCurrent);
                status.Text = Tf($"Renamed to {name}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = ex.Message; }
        }
    }

    private async Task Delete()
    {
        if (busy) return;
        var count = tvActive ? tvList.SelectedItems.Count : pcList.SelectedItems.Count;
        if (count == 0) { status.Text = tvActive ? T("Nothing is selected on the TV.") : T("Nothing is selected on this PC."); return; }
        var answer = MessageBox.Show(this, Tf($"Delete {count} selected items? This cannot be undone."), T("Delete"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2,
            IsArabic ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
        if (answer != DialogResult.Yes) return;
        if (tvActive)
        {
            var items = SelectedTvItems();
            await RunTv(T("Deleting…"), async token =>
            {
                await Network.TvDeleteItems(tv, items.Select(item => item.Path).ToArray(), token);
                return Tf($"{items.Count} items deleted.");
            });
        }
        else
        {
            var paths = SelectedPcPaths();
            try
            {
                foreach (var path in paths)
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    else File.Delete(path);
                }
                LoadPc(pcCurrent);
                status.Text = Tf($"{paths.Count} items deleted.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = ex.Message; }
        }
    }

    private void Copy(bool move)
    {
        if (busy) return;
        if (tvActive)
        {
            var items = SelectedTvItems();
            if (items.Count == 0) { status.Text = T("Nothing is selected on the TV."); return; }
            tvClip = new(items, move);
            status.Text = move
                ? Tf($"Cut {items.Count} items. Choose a folder and Paste.")
                : Tf($"Copied {items.Count} items. Choose a folder and Paste.");
        }
        else
        {
            var paths = SelectedPcPaths();
            if (paths.Count == 0) { status.Text = T("Nothing is selected on this PC."); return; }
            pcClip = new(paths, move);
            status.Text = move
                ? Tf($"Cut {paths.Count} items. Choose a folder and Paste.")
                : Tf($"Copied {paths.Count} items. Choose a folder and Paste.");
        }
        UpdateActions();
    }

    private async Task Paste()
    {
        if (busy) return;
        if (tvActive)
        {
            if (tvClip is null) { status.Text = T("Copy or cut items first."); return; }
            var clip = tvClip;
            await RunTv(clip.Cut ? T("Moving…") : T("Copying…"), async token =>
            {
                var count = await Network.TvPaste(tv, clip.Items, tvCurrent, clip.Cut, token);
                if (clip.Cut) tvClip = null;
                return Tf($"{count} items pasted.");
            });
        }
        else
        {
            if (pcClip is null) { status.Text = T("Copy or cut items first."); return; }
            var clip = pcClip;
            try
            {
                foreach (var path in clip.Items) PasteLocal(path, pcCurrent, clip.Cut);
                if (clip.Cut) pcClip = null;
                LoadPc(pcCurrent);
                status.Text = Tf($"{clip.Items.Count} items pasted.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = ex.Message; }
            UpdateActions();
        }
    }

    private static void PasteLocal(string source, string folder, bool move)
    {
        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
        if (move && Path.GetDirectoryName(source) == folder) return;
        var destination = UniquePath(Path.Combine(folder, name));
        if (move)
        {
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
        }
        else if (Directory.Exists(source)) CopyDirectory(source, destination);
        else File.Copy(source, destination, false);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), false);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var attempt = 2; attempt <= 9999; attempt++)
        {
            var candidate = Path.Combine(directory, stem + $" ({attempt})" + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException(T("No unused destination file name could be found."));
    }

    private async Task SendToTv()
    {
        if (busy) return;
        var paths = SelectedPcPaths();
        if (paths.Count == 0) { status.Text = T("Choose files on this PC first."); return; }
        BeginWork(T("Sending to the TV…"));
        var token = work!.Token;
        try
        {
            var updates = new Progress<FileTransferProgress>(update =>
            {
                if (IsDisposed) return;
                progress.Style = ProgressBarStyle.Blocks;
                progress.Value = update.Total == 0 ? (update.Saved ? 1000 : 0)
                    : (int)Math.Clamp((update.Completed + (double)update.Sent) / update.Total * 1000, 0, 1000);
                status.Text = Tf($"File {update.Index} of {update.Count}: {update.Name} — {Bytes(update.Sent)} / {Bytes(update.Size)}");
            });
            var saved = await Network.UploadItems(tv, paths, tvCurrent, updates, token);
            if (IsDisposed || token.IsCancellationRequested) return;
            await ReadTvAsync(tvCurrent, token, false);
            status.Text = Tf($"{saved.Count} files sent to {tvCurrent}.");
        }
        catch (Exception ex) when (TransferFailure(ex)) { ShowFailure(ex, token); }
        finally { EndWork(); }
    }

    private async Task DownloadToPc()
    {
        if (busy) return;
        var items = SelectedTvItems();
        if (items.Count == 0) { status.Text = T("Nothing is selected on the TV."); return; }
        if (pcCurrent.Length == 0) { status.Text = T("This PC folder is not available."); return; }
        BeginWork(T("Downloading from the TV…"));
        var token = work!.Token;
        try
        {
            var updates = new Progress<FileTransferProgress>(update =>
            {
                if (IsDisposed) return;
                progress.Style = ProgressBarStyle.Blocks;
                progress.Value = update.Total == 0 ? (update.Saved ? 1000 : 0)
                    : (int)Math.Clamp((update.Completed + (double)update.Sent) / update.Total * 1000, 0, 1000);
                status.Text = Tf($"Downloading {update.Index} of {update.Count}: {update.Name}");
            });
            var files = await Network.DownloadItems(tv, items, pcCurrent, updates, token);
            if (IsDisposed || token.IsCancellationRequested) return;
            LoadPc(pcCurrent);
            status.Text = Tf($"{files} files downloaded to {pcCurrent}.");
        }
        catch (Exception ex) when (TransferFailure(ex)) { ShowFailure(ex, token); }
        finally { EndWork(); }
    }

    private List<TvItem> SelectedTvItems() =>
        tvList.SelectedItems.Cast<ListViewItem>().Select(entry => (TvItem)entry.Tag!).ToList();

    private List<string> SelectedPcPaths() =>
        pcList.SelectedItems.Cast<ListViewItem>().Select(entry => (string)entry.Tag!).ToList();

    private string? Prompt(string title, string label, string initial, bool forTv)
    {
        using var dialog = new NamePrompt(title, label, initial, value => NameProblem(value, forTv));
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Value : null;
    }

    private static string? NameProblem(string value, bool forTv)
    {
        var name = value.Trim();
        if (name.Length == 0) return T("Enter a name.");
        if (forTv)
        {
            try { Network.TransferName(name); return null; }
            catch (IOException ex) { return ex.Message; }
        }
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return T("This name cannot be used.");
        return null;
    }

    private static string Bytes(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GiB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MiB"
        : bytes >= 1L << 10 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes} B";

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            closeAfterCancel = true;
            stop.Enabled = false;
            status.Text = T("Stopping…");
            work?.Cancel();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !lifetime.IsCancellationRequested) { lifetime.Cancel(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record PcDrive(string Label, string Root)
    {
        public override string ToString() => Label;
    }
}

sealed record TvClipboard(List<TvItem> Items, bool Cut);
sealed record PcClipboard(List<string> Items, bool Cut);

sealed class FileList : ListView
{
    internal FileList()
    {
        DoubleBuffered = true;
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = true;
        BorderStyle = BorderStyle.FixedSingle;
        RightToLeft = RightToLeft.No;
        ShowItemToolTips = true;
    }
}

sealed class NamePrompt : FluentForm
{
    internal string? Value { get; private set; }

    internal NamePrompt(string title, string label, string initial, Func<string, string?> validate)
    {
        Text = title; Icon = Program.AppIcon;
        ClientSize = new Size(440, 190); MinimumSize = new Size(360, 190);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No; RightToLeftLayout = IsArabic;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 26, 34, 44 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill });
        var input = new TextBox { Text = initial, MaxLength = 200, AccessibleName = label };
        layout.Controls.Add(input);
        var ok = new FluentButton { Text = T("OK"), AutoSize = true, Accent = true };
        var cancel = new FluentButton { Text = T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            var issue = validate(input.Text);
            if (issue is not null) { MessageBox.Show(this, issue, Text); return; }
            Value = input.Text.Trim();
            DialogResult = DialogResult.OK;
        };
        Shown += (_, _) => { input.Focus(); input.SelectAll(); };
        PrepareLayout();
    }
}
