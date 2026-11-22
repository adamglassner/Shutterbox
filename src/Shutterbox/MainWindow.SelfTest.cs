using System.Diagnostics;
using System.Windows.Controls;
using Shutterbox.Library;
using Shutterbox.Services;
using Shutterbox.Views;

namespace Shutterbox;

/// <summary>
/// Automated end-to-end test, run by the build with:  Shutterbox.exe --self-test out.txt [screenshot-folder]
/// Makes a folder of sample photos (with real EXIF dates, cameras, orientation and GPS), then drives the real
/// window: scanning, the timeline, thumbnails, details, search, favorites, tags, albums, rotation, Trash,
/// saving, rescanning, importing from a "camera card", the viewer and slideshow, settings and both themes.
/// Everything happens in a temporary folder; the user's own settings and library are never touched.
/// </summary>
public partial class MainWindow
{
    private readonly List<string> _testLog = new();
    private int _testFails;
    private string _shotDir;

    private sealed class TestFailure : Exception
    {
        public TestFailure(string message) : base(message) { }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new TestFailure(message);
    }

    private static void AreEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new TestFailure($"{what}: expected \"{expected}\", got \"{actual}\"");
    }

    private void Log(string line)
    {
        _testLog.Add(line);
        try { File.AppendAllText(App.SelfTestOutput, line + "\r\n"); } catch { }
    }

    private async Task Test(string name, Func<Task> body)
    {
        var sw = Stopwatch.StartNew();
        App.TestName = name;
        string line;
        try
        {
            await body();
            line = $"PASS {name} ({sw.ElapsedMilliseconds} ms)";
        }
        catch (Exception ex)
        {
            _testFails++;
            var msg = ex is TestFailure ? ex.Message : ex.ToString();
            line = $"FAIL {name}: {msg.Replace("\r", " ").Replace("\n", " ")}";
            if (Thumbnails.LastError != null) line += $" [last picture error: {Thumbnails.LastError}]";
            // Put things back so one failure doesn't knock over the tests after it
            try
            {
                if (ViewerOpen) CloseViewer();
                SearchBox.Text = "";
                if (_view != LibraryView.Photos) ShowView(LibraryView.Photos);
                ClearSelection();
                SelectionChanged();
            }
            catch { }
        }
        Log(line);
    }


    private Task Test(string name, Action body) => Test(name, () => { body(); return Task.CompletedTask; });

    private async Task Idle()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(120);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TestFailure("timed out waiting for " + what);
            await Task.Delay(60);
        }
    }

    /// <summary>Waits until the thumbnails of the photos on screen are made.</summary>
    private async Task WaitForThumbs(int count = 24)
    {
        var some = _flat.Take(count).Select(i => i.Photo).ToList();
        await WaitUntil(() => some.All(p => p.Unreadable || Thumbnails.HasFailed(p) || Thumbnails.Get(p, null) != null), 30000, "thumbnails");
        await Idle();
    }

    private async Task RunSelfTestAsync()
    {
        _shotDir = App.SelfTestShots;
        if (_shotDir != null) Directory.CreateDirectory(_shotDir);
        try { File.WriteAllText(App.SelfTestOutput, $"Shutterbox {AppInfo.Version} self-test\r\n"); } catch { }
        WindowState = WindowState.Normal;
        Left = 0; Top = 0;
        // The build machine's screen is small (1024×768) and Windows won't make the window bigger than that,
        // so lay the window's content out at a typical laptop size instead.
        RootGrid.Width = 1440;
        RootGrid.Height = 900;
        RootGrid.HorizontalAlignment = HorizontalAlignment.Left;
        RootGrid.VerticalAlignment = VerticalAlignment.Top;
        await Idle();
        ApplyInfoPanelVisibility();
        Log($"INFO window {ActualWidth:0}×{ActualHeight:0}, content {RootGrid.ActualWidth:0}×{RootGrid.ActualHeight:0}, screen {SystemParameters.PrimaryScreenWidth:0}×{SystemParameters.PrimaryScreenHeight:0}");

        var root = App.SelfTestRoot;
        var pics = IOPath.Combine(root, "Pictures");
        var card = IOPath.Combine(root, "CameraCard", "DCIM", "100CANON");
        int expected = 0;

        // ---------------------------------------------------------------- parsing
        await Test("Dates from file names", () =>
        {
            AreEqual(new DateTime(2023, 10, 14, 18, 30, 11), MetadataReader.FromFileName("IMG_20231014_183011.jpg") ?? default, "IMG_20231014_183011");
            AreEqual(new DateTime(2023, 10, 14, 18, 30, 11), MetadataReader.FromFileName("Screenshot 2023-10-14 183011.png") ?? default, "Screenshot 2023-10-14 183011");
            AreEqual(new DateTime(2023, 10, 14, 18, 30, 11), MetadataReader.FromFileName("2023-10-14 18.30.11.jpg") ?? default, "2023-10-14 18.30.11");
            AreEqual(new DateTime(2024, 1, 2), (MetadataReader.FromFileName("PXL_20240102_030405678.jpg") ?? default).Date, "PXL_20240102_030405678");
            Assert(MetadataReader.FromFileName("DSC01234.jpg") == null, "DSC01234 has no date");
            Assert(MetadataReader.FromFileName("IMG_20231345.jpg") == null, "month 13 isn't a date");
            AreEqual(0.002, MetadataReader.ToDouble(1UL | (500UL << 32)) ?? 0, "EXIF rational 1/500");
            AreEqual(new DateTime(2026, 10, 11, 18, 42, 5), MetadataReader.ParseDate("2026:10:11 18:42:05\0") ?? default, "EXIF date string");
        });

        // ---------------------------------------------------------------- empty library
        await Test("Empty library", async () =>
        {
            await Idle();
            AreEqual(0, Lib.Folders.Count, "folders");
            Assert(EmptyState.Visibility == Visibility.Visible, "the welcome screen shows");
            Assert(AddPicturesBtn.Visibility == Visibility.Visible, "offers to add the Pictures folder");
            Shot("01-welcome");
        });

        // ---------------------------------------------------------------- sample photos
        await Test("Make sample photos", () =>
        {
            expected = SamplePhotos.CreateLibrary(pics);
            SamplePhotos.CreateCameraCard(card, pics);
            Assert(expected >= 40, $"made {expected} sample pictures");
        });

        await Test("Read photo details", () =>
        {
            var p = MetadataReader.Read(IOPath.Combine(pics, "Big Sur trip", "PXL_20261011_184200.jpg"));
            Assert(!p.Unreadable, "sample JPEG is readable");
            AreEqual(1600, p.Width, "width");
            AreEqual(1067, p.Height, "height");
            AreEqual("exif", p.TakenSource, "date source");
            AreEqual(new DateTime(2026, 10, 11, 18, 42, 0), p.Taken, "date taken");
            AreEqual("Pixel 9 Pro", p.Model, "camera model");
            Log($"INFO exposure {p.ExposureText}; focal {p.FocalLength}; lens {p.Lens}; location {p.LocationText}");

            var side = MetadataReader.Read(IOPath.Combine(pics, "Big Sur trip", "PXL_20261011_190000.jpg"));
            AreEqual(6, side.Orientation, "EXIF orientation of the portrait shot");
            Assert(side.DisplayWidth < side.DisplayHeight, $"portrait shot shows taller than wide ({side.DisplayWidth}×{side.DisplayHeight})");

            var shot = MetadataReader.Read(IOPath.Combine(pics, "Screenshot 2026-09-02 101500.png"));
            AreEqual("name", shot.TakenSource, "PNG date comes from its name");
            AreEqual(new DateTime(2026, 9, 2, 10, 15, 0), shot.Taken, "PNG date");

            var broken = MetadataReader.Read(IOPath.Combine(pics, "Home", "broken.jpg"));
            Assert(broken.Unreadable, "a damaged file is marked unreadable");
        });

        // ---------------------------------------------------------------- scanning
        await Test("Add folder and scan", async () =>
        {
            await AddFolderToLibrary(pics);
            await Idle();
            AreEqual(1, Lib.Folders.Count, "library folders");
            AreEqual(expected, Lib.Count, "photos found");
            AreEqual(expected, _flat.Count, "photos on the timeline");
            Assert(EmptyState.Visibility != Visibility.Visible, "welcome screen is gone");
            Assert(!Lib.Photos.Any(p => p.Path.EndsWith(".txt")), "text files are ignored");
            Assert(Lib.Photos.Count(p => p.Unreadable) == 1, "one unreadable file");
            // newest first
            for (int i = 1; i < _flat.Count; i++)
                Assert(_flat[i - 1].Photo.Taken >= _flat[i].Photo.Taken, "timeline is newest first");
            AreEqual(expected.ToString("N0"), CountPhotos.Text, "sidebar count");
        });

        await Test("Timeline groups by day, month and year", async () =>
        {
            var visible = Lib.Photos.Where(p => !p.IsTrashed).ToList();
            AreEqual(visible.Select(p => p.Taken.Date).Distinct().Count(), _rows.OfType<HeaderRow>().Count(), "day groups");
            var big = _rows.OfType<HeaderRow>().FirstOrDefault(h => h.Title.Contains("Oct 11"));
            Assert(big != null, "a group for Oct 11 (" + string.Join(" | ", _rows.OfType<HeaderRow>().Select(h => h.Title)) + ")");
            AreEqual("Big Sur trip", big.Subtitle, "group subtitle is the folder name");
            AreEqual("10 photos", big.CountText, "group count");
            Assert(_cols >= 4, $"several photos per row ({_cols})");
            foreach (var r in _rows.OfType<PhotoRow>()) Assert(r.Items.Count <= _cols, "rows aren't over-full");

            SetZoom(TimelineZoom.Months);
            await Idle();
            AreEqual(visible.Select(p => (p.Taken.Year, p.Taken.Month)).Distinct().Count(), _rows.OfType<HeaderRow>().Count(), "month groups");
            Assert(_rows.OfType<HeaderRow>().Any(h => h.Title == "October 2026"), "October 2026 group");
            await WaitForThumbs(60);
            Shot("03-months");

            SetZoom(TimelineZoom.Years);
            await Idle();
            AreEqual(visible.Select(p => p.Taken.Year).Distinct().Count(), _rows.OfType<HeaderRow>().Count(), "year groups");
            await WaitForThumbs(80);
            Shot("04-years");

            SetZoom(TimelineZoom.Days);
            await Idle();
        });

        await Test("Thumbnails", async () =>
        {
            await WaitForThumbs();
            var files = Directory.Exists(Thumbnails.Folder) ? Directory.GetFiles(Thumbnails.Folder, "*.jpg", SearchOption.AllDirectories).Length : 0;
            Assert(files >= 10, $"thumbnails saved to the cache ({files})");
            var side = Lib.Photos.First(p => p.FileName == "PXL_20261011_190000.jpg");
            BitmapSource t = null;
            await WaitUntil(() => (t = Thumbnails.Get(side, null)) != null, 15000, "portrait thumbnail");
            Assert(t.PixelHeight > t.PixelWidth, $"portrait thumbnail is upright ({t.PixelWidth}×{t.PixelHeight})");
            var broken = Lib.Photos.First(p => p.FileName == "broken.jpg");
            Assert(ItemFor(broken).FailedVisibility == Visibility.Visible, "damaged file shows a warning tile");
        });

        await Test("Select and details", async () =>
        {
            var target = _flat.First(i => i.Photo.FileName == "PXL_20261011_184200.jpg");
            SelectOnly(target);
            await Idle();
            Assert(InfoSingle.Visibility == Visibility.Visible, "details panel shows one photo");
            AreEqual("PXL_20261011_184200.jpg", InfoName.Text, "details name");
            Assert(InfoDate.Text.Contains("October 11, 2026"), "details date: " + InfoDate.Text);
            await WaitUntil(() => PreviewImage.Source is BitmapSource b && b.PixelWidth >= 600, 15000, "sharp preview");
            await WaitForThumbs();
            Shot("02-library");

            // keyboard: right arrow moves, shift+right extends
            MoveFocus(1, System.Windows.Input.ModifierKeys.None);
            AreEqual(1, _selected.Count, "arrow key keeps one selected");
            Assert(_focusId != target.Photo.Id, "arrow key moved");
            MoveFocus(1, System.Windows.Input.ModifierKeys.Shift);
            AreEqual(2, _selected.Count, "shift+arrow extends the selection");
            await Idle();
            Assert(SelectionBar.Visibility == Visibility.Visible, "selection bar shows");
            Assert(InfoMulti.Visibility == Visibility.Visible, "details panel shows several photos");
            var first = _rows.OfType<HeaderRow>().First();
            GroupSelect(first);
            Assert(first.Items.All(i => i.IsSelected), "Select on a day selects the whole day");
            await Idle();
            Shot("05-selection");
            ClearSelection();
            SelectionChanged();
        });

        await Test("Search", async () =>
        {
            async Task<int> Find(string q) { SearchBox.Text = q; RebuildView(false); await Idle(); return _flat.Count; }
            AreEqual(Lib.Photos.Count(p => p.Model == "Pixel 9 Pro"), await Find("pixel"), "search by camera");
            AreEqual(8, await Find("portland"), "search by folder");
            AreEqual(8, await Find("canon r6"), "two words");
            AreEqual(6, await Find("summer"), "search by folder");
            AreEqual(Lib.Photos.Count(p => p.Taken.Year == 2025), await Find("2025"), "search by year");
            AreEqual(0, await Find("zebra"), "no matches");
            Assert(EmptyState.Visibility == Visibility.Visible && EmptyTitle.Text == "No matches", "no-matches message");
            await Find("");
            AreEqual(expected, _flat.Count, "search cleared");
        });

        await Test("Favorites", async () =>
        {
            var three = _flat.Skip(2).Take(3).Select(i => i.Photo).ToList();
            ToggleFavorite(three);
            Assert(three.All(p => p.Favorite), "marked as favorites");
            ShowView(LibraryView.Favorites);
            await Idle();
            AreEqual(3, _flat.Count, "Favorites view");
            AreEqual("3", CountFavorites.Text, "sidebar count");
            ToggleFavorite(new List<Photo> { three[0] });
            await Task.Delay(450);
            await Idle();
            AreEqual(2, _flat.Count, "unfavorited photo leaves Favorites");
            ShowView(LibraryView.Photos);
            await Idle();
        });

        await Test("Tags and captions", async () =>
        {
            var p = _flat[0].Photo;
            Lib.AddTag(new[] { p }, "sunset");
            Lib.AddTag(new[] { p }, "Sunset"); // same tag, different case
            AreEqual(1, p.Tags.Count, "tags aren't duplicated");
            Lib.SetCaption(p, "  Last light at the beach  ");
            AreEqual("Last light at the beach", p.Caption, "caption trimmed");
            SearchBox.Text = "sunset";
            RebuildView(false);
            await Idle();
            AreEqual(1, _flat.Count, "search finds the tag");
            SearchBox.Text = "beach";
            RebuildView(false);
            AreEqual(1, _flat.Count, "search finds the caption");
            SearchBox.Text = "";
            RebuildView(false);
            await Idle();
        });

        Album album = null;
        await Test("Albums", async () =>
        {
            var bigSur = Lib.Photos.Where(p => p.FolderName == "Big Sur trip").OrderBy(p => p.Taken).Take(5).ToList();
            album = NewAlbum("Big Sur", bigSur);
            AreEqual(5, Lib.PhotosIn(album).Count(), "album has the photos");
            AreEqual(0, Lib.AddToAlbum(album, bigSur.Take(2)), "adding again doesn't duplicate");
            var second = NewAlbum("Big Sur", null);
            AreEqual("Big Sur (2)", second.Name, "album names are unique");
            Lib.RenameAlbum(second, "Family");
            AreEqual(2, AlbumNav.Children.Count, "albums in the sidebar");
            ShowView(LibraryView.Albums);
            await Idle();
            AreEqual(3, ((IEnumerable<AlbumCard>)AlbumGrid.ItemsSource).Count(), "album cards (+ new album)");
            await WaitUntil(() => Thumbnails.Get(Lib.CoverOf(album), null) != null, 10000, "album cover");
            await Idle();
            Shot("06-albums");
            ShowView(LibraryView.Album, albumId: album.Id);
            await Idle();
            AreEqual(5, _flat.Count, "album view");
            AreEqual("Big Sur", TitleText.Text, "album title");
            Assert(Banner.Visibility == Visibility.Visible, "album banner");
            RemoveFromAlbum(album, new List<Photo> { bigSur[0] });
            await Task.Delay(450);
            await Idle();
            AreEqual(4, _flat.Count, "removed from album");
            Assert(!bigSur[0].IsTrashed && Lib.Get(bigSur[0].Id) != null, "photo is still in the library");
            await WaitForThumbs();
            Shot("07-album");
            ShowView(LibraryView.Photos);
            await Idle();
        });

        await Test("Rotate", async () =>
        {
            var p = Lib.Photos.First(x => x.FileName == "PXL_20261011_184200.jpg");
            var w = p.DisplayWidth;
            RotatePhotos(new List<Photo> { p }, 90);
            AreEqual(90, p.Rotation, "rotation saved");
            AreEqual(p.Height, p.DisplayWidth, "rotated width");
            BitmapSource t = null;
            await WaitUntil(() => (t = Thumbnails.Get(p, null)) != null, 15000, "rotated thumbnail");
            Assert(t.PixelHeight > t.PixelWidth, "rotated thumbnail is portrait");
            RotatePhotos(new List<Photo> { p }, -90);
            AreEqual(0, p.Rotation, "rotated back");
            AreEqual(w, p.DisplayWidth, "width restored");
        });

        await Test("Trash, restore and empty", async () =>
        {
            var two = Lib.Photos.Where(p => p.FolderName == "Home" && !p.Unreadable).OrderBy(p => p.Taken).Take(2).ToList();
            TrashPhotos(two);
            await Task.Delay(450);
            await Idle();
            AreEqual(expected - 2, _flat.Count, "trashed photos leave the timeline");
            AreEqual("2", CountTrash.Text, "Trash count");
            ShowView(LibraryView.Trash);
            await Idle();
            AreEqual(2, _flat.Count, "Trash view");
            Assert(Banner.Visibility == Visibility.Visible, "Trash explains itself");
            await WaitForThumbs();
            Shot("08-trash");
            RestorePhotos(new List<Photo> { two[0] });
            await Task.Delay(450);
            await Idle();
            AreEqual(1, _flat.Count, "restored one");
            Assert(File.Exists(two[1].Path), "file still there before emptying");
            EmptyTrash(null);
            await Task.Delay(450);
            await Idle();
            Assert(!File.Exists(two[1].Path), "emptying Trash removes the file (to the Recycle Bin)");
            AreEqual(0, _flat.Count, "Trash is empty");
            expected--;
            AreEqual(expected, Lib.Count, "library count after emptying");
            ShowView(LibraryView.Photos);
            await Idle();
        });

        await Test("Library is saved", () =>
        {
            Lib.Save();
            var copy = new LibraryStore(SettingsService.Folder);
            copy.Load();
            AreEqual(Lib.Count, copy.Count, "photos");
            AreEqual(2, copy.Photos.Count(p => p.Favorite), "favorites");
            AreEqual(2, copy.Albums.Count, "albums");
            AreEqual(4, copy.PhotosIn(copy.Albums.First(a => a.Name == "Big Sur")).Count(), "album photos");
            Assert(copy.Photos.Any(p => p.Tags.Contains("sunset") && p.Caption == "Last light at the beach"), "tags and captions");
            AreEqual(1, copy.Folders.Count, "folders");
        });

        await Test("Rescan notices new, changed and deleted files", async () =>
        {
            var extra = IOPath.Combine(pics, "Home", "IMG_9001.jpg");
            SamplePhotos.SaveJpeg(extra, SamplePhotos.Scene(1200, 800, 77), new DateTime(2026, 10, 9, 9, 0, 0), "Google", "Pixel 9 Pro");
            await StartScan(null);
            AreEqual(expected + 1, Lib.Count, "new file found");
            var added = Lib.GetByPath(extra);
            Assert(added != null && added.Taken == new DateTime(2026, 10, 9, 9, 0, 0), "new file read");
            Lib.SetFavorite(new[] { added }, true);
            SamplePhotos.SaveJpeg(extra, SamplePhotos.Scene(1000, 1000, 78), new DateTime(2026, 10, 9, 9, 30, 0), "Google", "Pixel 9 Pro");
            File.SetLastWriteTimeUtc(extra, DateTime.UtcNow.AddMinutes(1));
            await StartScan(null);
            Assert(added.Width == 1000 && added.Taken.Minute == 30, "changed file re-read");
            Assert(added.Favorite, "favorite kept when the file changes");
            File.Delete(extra);
            await StartScan(null);
            AreEqual(expected, Lib.Count, "deleted file forgotten");
            await Idle();
        });

        await Test("Videos have their own section", async () =>
        {
            var clips = IOPath.Combine(pics, "Clips");
            SampleVideo.WriteAvi(IOPath.Combine(clips, "VID_20261018_143000.avi"), 160, 120, 30, 10, 1);
            SampleVideo.WriteAvi(IOPath.Combine(clips, "beach.avi"), 160, 120, 20, 10, 2);
            int photosBefore = Lib.Photos.Count(p => !p.IsTrashed && !p.IsVideo);
            await StartScan(null);
            AreEqual(expected + 2, Lib.Count, "videos found");
            expected += 2;
            AreEqual(2, Lib.Photos.Count(p => p.IsVideo), "videos in the library");
            ShowView(LibraryView.Photos);
            await Idle();
            AreEqual(photosBefore, _flat.Count, "Photos shows photos only");
            Assert(!_flat.Any(i => i.Photo.IsVideo), "no videos among the photos");
            ShowView(LibraryView.Videos);
            await Idle();
            AreEqual(2, _flat.Count, "Videos view");
            AreEqual("2", CountVideos.Text, "sidebar video count");
            var v = Lib.Photos.First(p => p.FileName.StartsWith("VID_"));
            Log($"INFO video: {v.Width}×{v.Height}, length {v.DurationText}, date from {v.TakenSource}");
            if (v.TakenSource != "exif") AreEqual(new DateTime(2026, 10, 18, 14, 30, 0), v.Taken, "video date from its name");
            await WaitForThumbs(2);
            Log($"INFO video still made by Windows: {(Thumbnails.Get(v, null) != null ? "yes" : "no (" + (Thumbnails.HasFailed(v) ? "not available on this machine" : "pending") + ")")}");
            Assert(ItemFor(v).VideoVisibility == Visibility.Visible, "video badge on the tile");
            Shot("14-videos");
            OpenViewer(v);
            try { await WaitUntil(() => VideoOpened || ViewerMessage.Visibility == Visibility.Visible, 10000, "video to open"); }
            catch (TestFailure) { }
            Assert(VideoShowing || ViewerMessage.Visibility == Visibility.Visible, "the viewer shows a player (or says why it can't)");
            Log($"INFO video playback on the build machine: {(VideoOpened ? "plays" : "not available (" + ViewerMessageText.Text.Replace("\n", " ") + ")")}");
            await Task.Delay(400);
            Shot("15-video-player");
            CloseViewer();
            StartSlideshow(Lib.Photos.Where(p => !p.IsTrashed).ToList());
            Assert(!_viewList.Any(p => p.IsVideo), "slideshows skip videos");
            StopSlideshow();
            CloseViewer();
            ShowView(LibraryView.Photos);
            await Idle();
        });

        string recovery = null;
        List<Photo> lockedOriginals = null;
        await Test("Locked album: set up, add, lock and unlock", async () =>
        {
            Assert(!Vault.IsSetUp, "not set up at first");
            AreEqual("A PIN is 4 to 12 digits.", Vault.Validate("12", "PIN"), "short PIN refused");
            Assert(LockSetupDialog.Check("2468", "2469", "PIN") != null, "PINs that don't match are caught");
            ShowView(LibraryView.Locked);
            await Idle();
            Assert(LockedGate.Visibility == Visibility.Visible && GateSetup.Visibility == Visibility.Visible, "set-up screen");
            Shot("16-locked-welcome");
            var dlg = new LockSetupDialog(LockSetupDialog.Mode.SetUp) { Owner = this };
            dlg.Show();
            dlg.Secret1.Password = "2468";
            dlg.Secret2.Password = "2468";
            await Idle();
            ShotOf(dlg, "17-locked-setup");
            dlg.Close();

            recovery = await Task.Run(() => Vault.SetUp("2468", "PIN"));
            Assert(Vault.IsUnlocked, "unlocked after setting up");
            AreEqual(29, recovery.Length, "recovery key format");
            lockedOriginals = Lib.Photos.Where(p => p.FolderName == "Portland").OrderBy(p => p.FileName).Take(2).ToList();
            var clip = Lib.Photos.First(p => p.IsVideo);
            var r = await LockPhotosAsync(lockedOriginals.Append(clip).ToList());
            AreEqual(3, r.added, "added");
            AreEqual(0, r.errors.Count, "errors: " + string.Join("; ", r.errors));
            var again = await Vault.AddAsync(lockedOriginals);
            AreEqual(2, again.skipped, "adding again skips them");
            Assert(lockedOriginals.All(p => File.Exists(p.Path) && Lib.Get(p.Id) != null), "originals untouched");

            // really encrypted on disk: no JPEG or AVI headers, no names
            foreach (var f in Directory.GetFiles(IOPath.Combine(Vault.Folder, "items")))
            {
                var b = File.ReadAllBytes(f);
                AreEqual("SBOX1", System.Text.Encoding.ASCII.GetString(b, 0, 5), "encrypted file header");
                Assert(!(b[17 + 16] == 0xFF && b[17 + 17] == 0xD8), "not a readable JPEG");
                Assert(IOPath.GetFileNameWithoutExtension(f).Length == 32, "random file names");
            }
            var index = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(IOPath.Combine(Vault.Folder, "index.sbox")));
            Assert(!index.Contains("IMG_41") && !index.Contains("VID_") && !index.Contains("beach"), "names are encrypted too");
            var lp = Vault.Photos.First(p => p.FileName == lockedOriginals[0].FileName);
            Assert(Vault.ReadBytes(lp.VaultId).AsSpan().SequenceEqual(File.ReadAllBytes(lockedOriginals[0].Path)), "decrypts back exactly");

            ShowView(LibraryView.Locked);
            await Idle();
            AreEqual(3, _flat.Count, "Locked view");
            AreEqual("Photos|Videos", string.Join("|", _rows.OfType<HeaderRow>().Select(h => h.Title)), "photos and videos apart");
            await WaitForThumbs(3);
            SelectOnly(_flat[0]);
            await Idle();
            Assert(CaptionBox.Visibility == Visibility.Collapsed && InfoEditBtn.Visibility == Visibility.Collapsed, "no editing or sharing of locked items");
            Shot("18-locked-album");
            OpenViewer(_flat[0].Photo);
            await WaitUntil(() => ViewerImage.Source is BitmapSource b && b.PixelWidth >= 1000, 15000, "a locked photo in the viewer");
            Assert(ViewerShareBtn.Visibility == Visibility.Collapsed, "no sharing from the viewer");
            CloseViewer();

            LockNow();
            await Idle();
            Assert(!Vault.IsUnlocked, "locked");
            AreEqual(0, Vault.Photos.Count, "nothing visible while locked");
            Assert(LockedGate.Visibility == Visibility.Visible && GateUnlock.Visibility == Visibility.Visible, "unlock screen");
            AreEqual(0, _flat.Count, "timeline empty while locked");
            Shot("19-locked-locked");
            Assert(!Vault.Unlock("1111"), "wrong PIN refused");
            GatePassword.Password = "2468";
            GateUnlock_Click(null, null);
            await WaitUntil(() => Vault.IsUnlocked, 20000, "unlocking from the lock screen");
            await Task.Delay(300);
            await Idle();
            AreEqual(3, _flat.Count, "unlocked again");

            Vault.Lock();
            Assert(Vault.UnlockWithRecoveryKey(recovery.ToLowerInvariant().Replace("-", " ")), "recovery key works (any case or spacing)");
            Vault.ChangeSecret("hunter22", "Password");
            Vault.Lock();
            Assert(!Vault.Unlock("2468"), "old PIN no longer works");
            Assert(Vault.Unlock("hunter22"), "new password works");
            AreEqual("Password", Vault.Info.Kind, "now a password");
            var key = Vault.CopyKey();
            Vault.Lock();
            Assert(Vault.UnlockWithKey(key), "the key Windows Hello keeps unlocks it");
            Assert(!Vault.UnlockWithKey(new byte[32]), "a wrong key doesn't");
            Log($"INFO Windows Hello available on the build machine: {await WindowsHello.IsAvailableAsync()}");
            Assert(!WindowsHello.IsOn, "Windows Hello is off until turned on");
        });

        await Test("Locked album: tampering is detected", () =>
        {
            var key = Vault.CopyKey();
            var f = Directory.GetFiles(IOPath.Combine(Vault.Folder, "items")).First();
            var b = File.ReadAllBytes(f);
            Vault.DecryptBytes(b, key);
            var changed = b.ToArray();
            changed[changed.Length / 2] ^= 0x40;
            bool caught = false;
            try { Vault.DecryptBytes(changed, key); } catch (System.Security.Cryptography.CryptographicException) { caught = true; }
            Assert(caught, "a changed byte is caught");
            caught = false;
            try { Vault.DecryptBytes(b.Take(b.Length - 10).ToArray(), key); } catch (Exception) { caught = true; }
            Assert(caught, "a cut-short file is caught");
            // a big file goes through in 1 MB pieces
            var big = new byte[3 * 1024 * 1024 + 123];
            new Random(5).NextBytes(big);
            Assert(Vault.DecryptBytes(Vault.EncryptBytes(big, key), key).AsSpan().SequenceEqual(big), "multi-piece round trip");
        });

        await Test("Locked album: restore and delete", async () =>
        {
            var restoreDir = IOPath.Combine(root, "Restored");
            var lp = Vault.Photos.First(p => p.FileName == lockedOriginals[1].FileName);
            var n = await RestoreFromLockedAsync(new List<Photo> { lp }, restoreDir);
            AreEqual(1, n, "restored");
            var restored = IOPath.Combine(restoreDir, lp.FileName);
            Assert(File.Exists(restored) && File.ReadAllBytes(restored).AsSpan().SequenceEqual(File.ReadAllBytes(lockedOriginals[1].Path)), "restored file is identical");
            AreEqual(2, Vault.Count, "taken out of the Locked album");
            DeleteFromLocked(new List<Photo> { Vault.Photos.First(p => p.IsVideo) });
            AreEqual(1, Vault.Count, "deleted");
            AreEqual(1, Directory.GetFiles(IOPath.Combine(Vault.Folder, "items")).Length, "its encrypted file is gone");
        });

        await Test("Locked album: move to a cloud folder and open it on another PC", async () =>
        {
            var cloud = IOPath.Combine(root, "OneDrive");
            Directory.CreateDirectory(cloud);
            var problem = await Vault.MoveToAsync(cloud);
            Assert(problem == null, problem);
            AreEqual(IOPath.Combine(cloud, Vault.PortableFolderName), Vault.Folder, "new location");
            Assert(!Directory.Exists(Vault.DefaultFolder), "old copy removed");
            Vault.Lock();
            Assert(Vault.Unlock("hunter22"), "unlocks in its new place");
            AreEqual(1, Vault.Count, "contents moved");

            // "another PC": a copy of the folder, and a Shutterbox that has never seen it
            var otherParent = IOPath.Combine(root, "OtherPC");
            var other = IOPath.Combine(otherParent, Vault.PortableFolderName);
            foreach (var f in Directory.GetFiles(Vault.Folder, "*", SearchOption.AllDirectories))
            {
                var dest = IOPath.Combine(other, IOPath.GetRelativePath(Vault.Folder, f));
                Directory.CreateDirectory(IOPath.GetDirectoryName(dest));
                File.Copy(f, dest);
            }
            Vault.Lock();
            S.LockedFolder = "";
            Assert(!Vault.IsSetUp, "a new PC has no Locked album");
            var p2 = Vault.OpenFolder(otherParent);
            Assert(p2 == null, p2);
            Assert(Vault.IsSetUp && !Vault.IsUnlocked, "opened, still locked");
            Assert(Vault.Unlock("hunter22"), "unlocks with the same password");
            AreEqual(1, Vault.Count, "same contents");
            ShowView(LibraryView.Settings);
            await Idle();
            Shot("20-settings-locked");
            ShowView(LibraryView.Photos);
            await Idle();
        });

        await Test("Selecting lots of photos is easy", async () =>
        {
            ShowView(LibraryView.Photos);
            await Idle();
            // Select mode: clicks add, dragging across paints
            SetSelectMode(true);
            await Idle();
            Assert(SelectionBar.Visibility == Visibility.Visible, "select mode shows the selection bar");
            Assert(_flat[0].CheckVisibility == Visibility.Visible, "check circles show in select mode");
            BeginPaint(_flat[0], true);
            PaintOver(_flat[1]);
            PaintOver(_flat[2]);
            EndPaint();
            AreEqual(3, _selected.Count, "dragging across three tiles");
            BeginPaint(_flat[1], false);
            EndPaint();
            AreEqual(2, _selected.Count, "a click in select mode removes");
            await Idle();
            Shot("21-select-mode");
            SelectAll();
            AreEqual(_flat.Count, _selected.Count, "Select all");
            ClearSelection();
            SetSelectMode(false);
            SelectionChanged();
            Assert(_flat[0].CheckVisibility == Visibility.Collapsed, "check circles hide again");

            // A box dragged from empty space
            await Idle();
            var panel = TimelinePanel();
            var rows = panel.Children.OfType<ListBoxItem>().Where(li => li.DataContext is PhotoRow && li.IsVisible)
                .Select(li => (row: (PhotoRow)li.DataContext, top: li.TranslatePoint(new Point(0, 0), Timeline).Y, h: li.ActualHeight))
                .Where(r => r.top >= 0).OrderBy(r => r.top).ToList();
            Assert(rows.Count >= 2, "two rows on screen");
            SimulateMarquee(new Point(2, rows[0].top + 6), new Point(Timeline.ActualWidth - 30, rows[0].top + rows[0].h - 12), false);
            AreEqual(rows[0].row.Items.Count, _selected.Count, "the box selects the row it covers");
            SimulateMarquee(new Point(2, rows[1].top + 6), new Point(60, rows[1].top + 20), true);
            AreEqual(rows[0].row.Items.Count + 1, _selected.Count, "Ctrl+box adds to the selection");
            // keyboard: Space toggles the photo you're on
            _focusId = rows[1].row.Items[0].Photo.Id;
            ToggleFocused();
            AreEqual(rows[0].row.Items.Count, _selected.Count, "Space removes it again");
            ClearSelection();
            SelectionChanged();
        });

        await Test("Locked album: albums inside it", async () =>
        {
            Assert(Vault.IsUnlocked, "unlocked");
            var (v1Items, v1Albums) = Vault.ParseIndex(System.Text.Encoding.UTF8.GetBytes("[{\"Id\":\"abc\",\"Name\":\"x.jpg\"}]"));
            Assert(v1Items.Count == 1 && v1Albums.Count == 0, "older Locked albums (no albums yet) still open");
            ShowView(LibraryView.Locked);
            var a = NewLockedAlbum("Private");
            AreEqual("Private", a.Name, "made");
            AreEqual("Private (2)", Vault.CreateAlbum("private").Name, "names stay unique");
            Vault.DeleteAlbum(Vault.Albums.First(x => x.Name == "Private (2)"));
            AreEqual(1, Vault.AddToAlbum(a, Vault.Photos), "added");
            await Idle();
            AreEqual(1, _flat.Count, "the album's view");
            Assert(LockedAlbumBar.Visibility == Visibility.Visible && LockedAlbumBar.Children.Count >= 3, "album tabs");
            var index = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(IOPath.Combine(Vault.Folder, "index.sbox")));
            Assert(!index.Contains("Private"), "album names are encrypted");
            Vault.Lock();
            Assert(Vault.Albums.Count == 0, "albums hidden while locked");
            Assert(Vault.Unlock("hunter22"), "unlock");
            var again = Vault.Albums.FirstOrDefault(x => x.Name == "Private");
            Assert(again != null && Vault.PhotosIn(again).Count() == 1, "albums are kept");
            Vault.RenameAlbum(again, "Just us");
            AreEqual("Just us", Vault.Albums.Single().Name, "renamed");
            _lockedAlbumId = again.Id;
            RebuildView(false);
            await Idle();
        });

        await Test("Copy to Locked and Move to Locked", async () =>
        {
            var album = Vault.Albums.Single();
            int before = Vault.Count;
            var copyOne = Lib.Photos.First(p => p.FolderName == "Big Sur trip" && !p.IsTrashed);
            var r1 = await LockPhotosAsync(new List<Photo> { copyOne }, move: false, albumId: album.Id);
            AreEqual(1, r1.added, "copied");
            Assert(File.Exists(copyOne.Path) && Lib.Get(copyOne.Id) != null, "Copy keeps the original");
            var two = Lib.Photos.Where(p => p.FolderName == "Summer").OrderBy(p => p.FileName).Take(2).ToList();
            var bytes = two.Select(p => File.ReadAllBytes(p.Path)).ToList();
            var r2 = await LockPhotosAsync(two, move: true, albumId: album.Id);
            AreEqual(2, r2.added, "moved");
            Assert(two.All(p => !File.Exists(p.Path)), "Move deletes the originals");
            Assert(two.All(p => Lib.Get(p.Id) == null), "…and takes them out of the library");
            expected -= 2;
            AreEqual(expected, Lib.Count, "library count after moving");
            AreEqual(before + 3, Vault.Count, "all three are in the Locked album");
            AreEqual(4, Vault.PhotosIn(album).Count(), "and in its album");
            var moved = Vault.Photos.First(p => p.FileName == two[0].FileName);
            Assert(Vault.ReadBytes(moved.VaultId).AsSpan().SequenceEqual(bytes[0]), "the moved photo is intact");
            await Idle();
            await WaitForThumbs(4);
            Shot("22-locked-albums");
            _lockedAlbumId = null;
            ShowView(LibraryView.Photos);
            await Idle();
        });

        await Test("Import from a camera card", async () =>
        {
            var dlg = new ImportDialog(this, new[] { card }) { Owner = this };
            dlg.Show();
            await dlg.AnalysisTask;
            await Idle();
            AreEqual(8, dlg.Plan.Count, "photos on the card");
            AreEqual(2, dlg.Plan.Duplicates, "already in the library");
            Assert(dlg.ImportBtn.IsEnabled && (string)dlg.ImportBtn.Content == "Import 6", "button says what it will do: " + dlg.ImportBtn.Content);
            ShotOf(dlg, "09-import");
            var r = await dlg.RunImportAsync();
            dlg.Close();
            Assert(r != null, "import ran");
            AreEqual(6, r.Copied, "copied");
            AreEqual(2, r.Skipped, "skipped");
            var day = IOPath.Combine(S.ImportFolder, "2026", "2026-11-01");
            AreEqual(6, Directory.Exists(day) ? Directory.GetFiles(day).Length : 0, "organized into a date folder");
            expected += 6;
            AreEqual(expected, Lib.Count, "imported photos are in the library");
            AreEqual(LibraryView.Recent, _view, "shows Recently added");
            Assert(Directory.GetFiles(card).Length == 8, "originals kept");

            var again = new ImportDialog(this, new[] { card }) { Owner = this };
            again.Show();
            await again.AnalysisTask;
            AreEqual(8, again.Plan.Duplicates, "second time, everything is already there");
            Assert(!again.ImportBtn.IsEnabled, "nothing to import");
            again.Close();
            ShowView(LibraryView.Photos);
            await Idle();
        });

        await Test("Viewer", async () =>
        {
            var start = _flat[3].Photo;
            OpenViewer(start);
            await Idle();
            Assert(ViewerOpen, "viewer opens");
            AreEqual(start.FileName, ViewerTitle.Text, "viewer title");
            AreEqual(3, ViewerIndex, "position");
            await WaitUntil(() => ViewerImage.Source is BitmapSource b && b.PixelWidth >= 1000, 15000, "full-size picture");
            ViewerStep(1);
            AreEqual(4, ViewerIndex, "next");
            ViewerStep(-1);
            AreEqual(3, ViewerIndex, "previous");
            await WaitUntil(() => ViewerImage.Source is BitmapSource b && b.PixelWidth >= 1000, 15000, "picture again");
            await Task.Delay(300);
            Shot("10-viewer");
            ZoomViewer(2.5);
            Assert(ViewerZoom > 2.4, "zoomed in");
            ResetZoom();
            AreEqual(1.0, ViewerZoom, "zoom reset");
            ViewerFavorite_Click(null, null);
            Assert(start.Favorite, "favorite from the viewer");
            ViewerFavorite_Click(null, null);
            StartSlideshowHere();
            Assert(SlideshowRunning, "slideshow runs");
            ViewerStep(1);
            await Task.Delay(800);
            StopSlideshow();
            Assert(!SlideshowRunning, "slideshow stops");
            CloseViewer();
            await Idle();
            Assert(!ViewerOpen, "viewer closes");
            AreEqual(1, _selected.Count, "the photo you ended on is selected");
        });

        await Test("Slideshow of an album", async () =>
        {
            StartSlideshow(Lib.PhotosIn(album).ToList());
            await Idle();
            Assert(ViewerOpen && SlideshowRunning, "album slideshow");
            AreEqual(4, _viewList.Count, "plays the album's photos");
            Assert(_viewList.Zip(_viewList.Skip(1)).All(z => z.First.Taken <= z.Second.Taken), "oldest first");
            StopSlideshow();
            CloseViewer();
            await Idle();
        });

        await Test("Settings page", async () =>
        {
            ShowView(LibraryView.Settings);
            await Idle();
            Assert(SettingsHost.Visibility == Visibility.Visible, "settings shows");
            Assert(_settingsPage.FolderList.Children.Count == 1, "folder listed");
            Shot("11-settings");
            S.Density = TileDensity.Large;
            SettingsService.Commit();
            ShowView(LibraryView.Photos);
            await Idle();
            var largeCols = _cols;
            S.Density = TileDensity.Compact;
            SettingsService.Commit();
            RebuildView(false);
            await Idle();
            Assert(_cols > largeCols, $"smaller thumbnails fit more per row ({largeCols} → {_cols})");
            S.Density = TileDensity.Comfortable;
            SettingsService.Commit();
            RebuildView(false);
            await Idle();
        });

        await Test("Light theme", async () =>
        {
            S.Theme = ThemeMode.Light;
            SettingsService.Commit();
            Assert(!ThemeManager.IsDark, "light theme on");
            ShowView(LibraryView.Photos);
            SelectOnly(_flat.First(i => i.Photo.FileName == "PXL_20261011_184200.jpg"));
            await WaitUntil(() => PreviewImage.Source is BitmapSource b && b.PixelWidth >= 600, 15000, "preview");
            await WaitForThumbs();
            Shot("12-library-light");
            ShowView(LibraryView.Settings);
            await Idle();
            Shot("13-settings-light");
            S.Theme = ThemeMode.Dark;
            SettingsService.Commit();
            ShowView(LibraryView.Photos);
            await Idle();
        });

        await Test("Remove folder", async () =>
        {
            RemoveFolder(pics);
            await Task.Delay(450);
            await Idle();
            AreEqual(0, Lib.Folders.Count, "folder removed");
            AreEqual(0, Lib.Count, "its photos are forgotten");
            Assert(Directory.GetFiles(pics, "*.jpg", SearchOption.AllDirectories).Length > 30, "the files are untouched");
            Assert(EmptyState.Visibility == Visibility.Visible, "back to the welcome screen");
        });

        Vault.Lock();
        if (Thumbnails.LastError != null) Log("INFO last picture error: " + Thumbnails.LastError);
        int fails = _testFails;
        Log($"\r\n{_testLog.Count(l => l.StartsWith("PASS") || l.StartsWith("FAIL")) - fails} passed, {fails} failed");
        Lib.Save();
        try { Directory.Delete(root, true); } catch { }
        Application.Current.Shutdown(fails == 0 ? 0 : 1);
    }

    private void GroupSelect(HeaderRow h)
    {
        bool all = h.Items.All(i => i.IsSelected);
        foreach (var it in h.Items) SetSelected(it, !all);
        SelectionChanged();
    }

    private void Shot(string name)
    {
        HideToast(); // keep screenshots clean
        if (Content is FrameworkElement element) Save(element, Background ?? Brushes.Black, name);
    }

    private void ShotOf(Window w, string name)
    {
        if (w.Content is FrameworkElement element) Save(element, w.Background ?? Brushes.White, name);
    }

    private void Save(FrameworkElement element, Brush background, string name)
    {
        if (_shotDir == null || element.ActualWidth < 1) return;
        try
        {
            element.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(element);
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bmp.Render(bg);
            bmp.Render(element);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(IOPath.Combine(_shotDir, name + ".png"));
            enc.Save(fs);
        }
        catch (Exception ex) { Log($"INFO screenshot {name} failed: {ex.Message}"); }
    }
}

/// <summary>Paints believable sample "photos" (sky, sun, sea, hills) and saves them with real EXIF data.</summary>
internal static class SamplePhotos
{
    private static readonly (string sky1, string sky2, string sun, string sea1, string sea2, string land)[] Palettes =
    {
        ("#F2A65A", "#FCE3B0", "#FFF4D6", "#3F5A6B", "#22313A", "#2A2620"),   // sunset
        ("#5E8FB8", "#CFE3F2", "#FFFFFF", "#2F6E8A", "#1C4255", "#3C4A3A"),   // clear day
        ("#7B5EA7", "#F3A27F", "#FFE0B8", "#3E3A63", "#232142", "#1E1B2E"),   // dusk
        ("#8FB1C9", "#E6EEF2", "#FFFFFF", "#6C8A7A", "#4D6650", "#3F5236"),   // overcast meadow
        ("#2C3E73", "#E58E5A", "#FFD39A", "#2B2F4A", "#151827", "#120F17"),   // late evening
        ("#A9CBE0", "#F4F7F2", "#FFFFFF", "#9DB089", "#728A5C", "#556B3E"),   // spring
    };

    public static BitmapSource Scene(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var pal = Palettes[seed % Palettes.Length];
        Color C(string s) => (Color)ColorConverter.ConvertFromString(s);
        double horizon = h * (0.52 + rnd.NextDouble() * 0.14);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(C(pal.sky1), C(pal.sky2), 90), null, new Rect(0, 0, w, horizon + 2));
            var sunX = w * (0.2 + rnd.NextDouble() * 0.6);
            var sunY = horizon * (0.45 + rnd.NextDouble() * 0.4);
            var r = h * (0.05 + rnd.NextDouble() * 0.05);
            var glow = new RadialGradientBrush(Color.FromArgb(150, C(pal.sun).R, C(pal.sun).G, C(pal.sun).B), Colors.Transparent);
            dc.DrawEllipse(glow, null, new Point(sunX, sunY), r * 4, r * 4);
            dc.DrawEllipse(new SolidColorBrush(C(pal.sun)), null, new Point(sunX, sunY), r, r);
            // far hills
            var far = new StreamGeometry();
            using (var g = far.Open())
            {
                g.BeginFigure(new Point(0, horizon), true, true);
                for (double x = 0; x <= w; x += w / 14.0)
                    g.LineTo(new Point(x, horizon - h * (0.03 + rnd.NextDouble() * 0.12)), true, true);
                g.LineTo(new Point(w, horizon), true, true);
            }
            var farColor = C(pal.land);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, farColor.R, farColor.G, farColor.B)), null, far);
            // water / field
            dc.DrawRectangle(new LinearGradientBrush(C(pal.sea1), C(pal.sea2), 90), null, new Rect(0, horizon, w, h - horizon));
            // sun reflection
            for (int i = 0; i < 9; i++)
            {
                double y = horizon + 6 + i * (h - horizon) / 12;
                double len = w * 0.08 * (1 - i / 12.0) * (0.6 + rnd.NextDouble());
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(120 - i * 10), 255, 240, 210)), null, new Rect(sunX - len / 2, y, len, 3));
            }
            // sea stacks / near hills
            int stacks = 1 + rnd.Next(3);
            for (int i = 0; i < stacks; i++)
            {
                double cx = w * (0.1 + rnd.NextDouble() * 0.8);
                double bw = w * (0.06 + rnd.NextDouble() * 0.1);
                double bh = h * (0.12 + rnd.NextDouble() * 0.2);
                var s = new StreamGeometry();
                using (var g = s.Open())
                {
                    g.BeginFigure(new Point(cx - bw, horizon + h * 0.06), true, true);
                    g.BezierTo(new Point(cx - bw * 0.8, horizon - bh), new Point(cx + bw * 0.6, horizon - bh * 1.1), new Point(cx + bw, horizon + h * 0.06), true, true);
                }
                dc.DrawGeometry(new SolidColorBrush(C(pal.land)), null, s);
            }
            // foreground shore
            var shore = new StreamGeometry();
            using (var g = shore.Open())
            {
                g.BeginFigure(new Point(0, h), true, true);
                g.LineTo(new Point(0, h * 0.86), true, true);
                g.BezierTo(new Point(w * 0.3, h * (0.8 + rnd.NextDouble() * 0.06)), new Point(w * 0.6, h * 0.95), new Point(w, h * (0.84 + rnd.NextDouble() * 0.08)), true, true);
                g.LineTo(new Point(w, h), true, true);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(235, 30, 26, 22)), null, shore);
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    public static void SaveJpeg(string path, BitmapSource bmp, DateTime taken, string make, string model, ushort orientation = 1, double? lat = null, double? lon = null)
    {
        Directory.CreateDirectory(IOPath.GetDirectoryName(path));
        var md = new BitmapMetadata("jpg");
        try { md.SetQuery("/app1/ifd/exif/{ushort=36867}", taken.ToString("yyyy:MM:dd HH:mm:ss")); } catch { }
        try { md.CameraManufacturer = make; } catch { }
        try { md.CameraModel = model; } catch { }
        if (orientation != 1) { try { md.SetQuery("/app1/ifd/{ushort=274}", orientation); } catch { } }
        try { md.SetQuery("/app1/ifd/exif/{ushort=33434}", 1UL | (500UL << 32)); } catch { }
        try { md.SetQuery("/app1/ifd/exif/{ushort=33437}", 17UL | (10UL << 32)); } catch { }
        try { md.SetQuery("/app1/ifd/exif/{ushort=34855}", (ushort)50); } catch { }
        try { md.SetQuery("/app1/ifd/exif/{ushort=37386}", 69UL | (10UL << 32)); } catch { }
        if (lat is double la && lon is double lo)
        {
            static ulong[] Dms(double v)
            {
                v = Math.Abs(v);
                var d = (ulong)Math.Floor(v);
                var m = (ulong)Math.Floor((v - d) * 60);
                var s = (ulong)Math.Round(((v - d) * 60 - m) * 60 * 100);
                return new[] { d | (1UL << 32), m | (1UL << 32), s | (100UL << 32) };
            }
            try
            {
                md.SetQuery("/app1/ifd/gps/{ushort=1}", la >= 0 ? "N" : "S");
                md.SetQuery("/app1/ifd/gps/{ushort=2}", Dms(la));
                md.SetQuery("/app1/ifd/gps/{ushort=3}", lo >= 0 ? "E" : "W");
                md.SetQuery("/app1/ifd/gps/{ushort=4}", Dms(lo));
            }
            catch { }
        }
        try
        {
            var enc = new JpegBitmapEncoder { QualityLevel = 86 };
            enc.Frames.Add(BitmapFrame.Create(bmp, null, md, null));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        catch
        {
            // metadata the encoder didn't accept: save the picture without it
            var enc = new JpegBitmapEncoder { QualityLevel = 86 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }

    private static void SavePng(string path, BitmapSource bmp)
    {
        Directory.CreateDirectory(IOPath.GetDirectoryName(path));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Makes the sample library. Returns how many picture files it made.</summary>
    public static int CreateLibrary(string pics)
    {
        int n = 0, seed = 1;
        // Big Sur: a sunset walk, one portrait shot stored sideways (orientation 6), with GPS
        var bigSur = IOPath.Combine(pics, "Big Sur trip");
        for (int i = 0; i < 10; i++)
        {
            var t = new DateTime(2026, 10, 11, 18, 0, 0).AddMinutes(i * 6 + 6);
            SaveJpeg(IOPath.Combine(bigSur, $"PXL_{t:yyyyMMdd_HHmmss}.jpg"), Scene(1600, 1067, seed++), t, "Google", "Pixel 9 Pro",
                orientation: (ushort)(i == 9 ? 6 : 1), lat: 36.2380 + i * 0.0003, lon: -121.8155);
            n++;
        }
        // Home: two days, plus a damaged file and a non-picture
        var home = IOPath.Combine(pics, "Home");
        for (int i = 0; i < 8; i++)
        {
            var t = new DateTime(2026, 10, 8, 9, 15, 0).AddMinutes(i * 37);
            SaveJpeg(IOPath.Combine(home, $"PXL_{t:yyyyMMdd_HHmmss}.jpg"), Scene(1200, 800, seed++), t, "Google", "Pixel 9 Pro");
            n++;
        }
        for (int i = 0; i < 4; i++)
        {
            var t = new DateTime(2026, 9, 20, 14, 0, 0).AddMinutes(i * 11);
            SaveJpeg(IOPath.Combine(home, $"PXL_{t:yyyyMMdd_HHmmss}.jpg"), Scene(1200, 800, seed++), t, "Google", "Pixel 9 Pro");
            n++;
        }
        File.WriteAllBytes(IOPath.Combine(home, "broken.jpg"), Enumerable.Range(0, 4000).Select(i => (byte)(i * 31 % 251)).ToArray());
        n++;
        File.WriteAllText(IOPath.Combine(home, "notes.txt"), "not a picture");
        // Portland: a different camera
        var portland = IOPath.Combine(pics, "Portland");
        for (int i = 0; i < 8; i++)
        {
            var t = new DateTime(2026, 10, 5, 11, 0, 0).AddMinutes(i * 9);
            SaveJpeg(IOPath.Combine(portland, $"IMG_{4100 + i:0000}.jpg"), Scene(1500, 1000, seed++), t, "Canon", "Canon EOS R6");
            n++;
        }
        // Last year, in a subfolder
        var summer = IOPath.Combine(pics, "2025", "Summer");
        for (int i = 0; i < 6; i++)
        {
            var t = new DateTime(2025, 7, 4, 16, 0, 0).AddMinutes(i * 23);
            SaveJpeg(IOPath.Combine(summer, $"IMG_{t:yyyyMMdd_HHmmss}.jpg"), Scene(1400, 1050, seed++), t, "Apple", "iPhone 15");
            n++;
        }
        // Screenshots: dates only in their names
        SavePng(IOPath.Combine(pics, "Screenshot 2026-09-02 101500.png"), Scene(1280, 720, seed++)); n++;
        SavePng(IOPath.Combine(pics, "Screenshot 2026-08-28 091200.png"), Scene(1280, 720, seed++)); n++;
        SavePng(IOPath.Combine(pics, "Screenshot 2026-08-28 093000.png"), Scene(1280, 720, seed++)); n++;
        return n;
    }

    /// <summary>A camera card: six new photos and two that are already in the library.</summary>
    public static void CreateCameraCard(string card, string pics)
    {
        Directory.CreateDirectory(card);
        for (int i = 0; i < 6; i++)
        {
            var t = new DateTime(2026, 11, 1, 15, 0, 0).AddMinutes(i * 14);
            SaveJpeg(IOPath.Combine(card, $"IMG_{5200 + i:0000}.jpg"), Scene(1500, 1000, 100 + i), t, "Canon", "Canon EOS R6");
        }
        foreach (var name in new[] { "IMG_4100.jpg", "IMG_4101.jpg" })
            File.Copy(IOPath.Combine(pics, "Portland", name), IOPath.Combine(card, name));
    }
}

/// <summary>Writes a small uncompressed AVI (moving colour bands) so the self-test has real videos to find.</summary>
internal static class SampleVideo
{
    public static void WriteAvi(string path, int w, int h, int frames, int fps, int seed)
    {
        Directory.CreateDirectory(IOPath.GetDirectoryName(path));
        int stride = (w * 3 + 3) & ~3;
        int frameBytes = stride * h;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        void Fcc(string s) => bw.Write(System.Text.Encoding.ASCII.GetBytes(s));
        long riffSizePos, hdrlSizePos, strlSizePos, moviSizePos;

        Fcc("RIFF"); riffSizePos = fs.Position; bw.Write(0); Fcc("AVI ");
        Fcc("LIST"); hdrlSizePos = fs.Position; bw.Write(0); Fcc("hdrl");
        Fcc("avih"); bw.Write(56);
        bw.Write(1_000_000 / fps); bw.Write(frameBytes * fps); bw.Write(0); bw.Write(0x10);
        bw.Write(frames); bw.Write(0); bw.Write(1); bw.Write(frameBytes); bw.Write(w); bw.Write(h);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        Fcc("LIST"); strlSizePos = fs.Position; bw.Write(0); Fcc("strl");
        Fcc("strh"); bw.Write(56);
        Fcc("vids"); Fcc("DIB "); bw.Write(0); bw.Write((short)0); bw.Write((short)0); bw.Write(0);
        bw.Write(1); bw.Write(fps); bw.Write(0); bw.Write(frames); bw.Write(frameBytes); bw.Write(-1); bw.Write(0);
        bw.Write((short)0); bw.Write((short)0); bw.Write((short)w); bw.Write((short)h);
        Fcc("strf"); bw.Write(40);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)24); bw.Write(0); bw.Write(frameBytes);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        Patch(bw, strlSizePos);
        Patch(bw, hdrlSizePos);

        Fcc("LIST"); moviSizePos = fs.Position; bw.Write(0); long moviStart = fs.Position; Fcc("movi");
        var offsets = new List<int>();
        var rnd = new Random(seed);
        byte r0 = (byte)rnd.Next(60, 255), g0 = (byte)rnd.Next(60, 255), b0 = (byte)rnd.Next(60, 255);
        var frame = new byte[frameBytes];
        for (int f = 0; f < frames; f++)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 3;
                    bool band = ((x + f * 4) / 16) % 2 == 0;
                    frame[i] = band ? b0 : (byte)(255 - b0);
                    frame[i + 1] = (byte)(g0 * y / h);
                    frame[i + 2] = band ? r0 : (byte)(x * 255 / w);
                }
            offsets.Add((int)(fs.Position - moviStart));
            Fcc("00db"); bw.Write(frameBytes); bw.Write(frame);
        }
        Patch(bw, moviSizePos);
        Fcc("idx1"); bw.Write(frames * 16);
        foreach (var o in offsets) { Fcc("00db"); bw.Write(0x10); bw.Write(o); bw.Write(frameBytes); }
        Patch(bw, riffSizePos);
    }

    private static void Patch(BinaryWriter bw, long sizePos)
    {
        long end = bw.BaseStream.Position;
        bw.BaseStream.Position = sizePos;
        bw.Write((int)(end - sizePos - 4));
        bw.BaseStream.Position = end;
    }
}
