using System.Windows.Controls;
using Shutterbox.Library;
using Shutterbox.Services;
using Shutterbox.Views;

namespace Shutterbox;

/// <summary>People (faces found on this PC) and searching by what's in a photo.</summary>
public partial class MainWindow
{
    private string _personId;
    private PeoplePage _peoplePage;
    private DispatcherTimer _peopleTimer;

    private void InitPeople()
    {
        var store = PeopleStore.Current;
        if (store == null) return;
        _peopleTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            _peopleTimer.Stop();
            RefreshPeopleCount();
            if (_view == LibraryView.People) { _peoplePage?.Refresh(); UpdateHeader(); }
            else if (_view == LibraryView.Person) RebuildView(true);
            else if (SearchBox.Text.Trim().Length > 0 && _view is not (LibraryView.Settings or LibraryView.Albums or LibraryView.Places)) RebuildView(true);
            else if (InfoShown) UpdateInfo();
        }, Dispatcher);
        _peopleTimer.Stop();
        store.Changed += () => { if (!_peopleTimer.IsEnabled) _peopleTimer.Start(); };
        Lib.Changed += c => { if (c.HasFlag(LibraryChange.Photos) || c.HasFlag(LibraryChange.Details)) Analyzer.Poke(); };
        SettingsService.Changed += Analyzer.Poke;
        if (!App.IsSelfTest) Analyzer.Start(Dispatcher);
    }

    private void RefreshPeopleCount()
    {
        var store = PeopleStore.Current;
        if (store == null) { CountPeople.Text = ""; return; }
        var n = store.People(id => Lib.Get(id) is Photo p && !p.IsTrashed).Count;
        CountPeople.Text = n > 0 ? n.ToString("N0") : "";
    }

    private void ShowPeopleHost(bool show)
    {
        if (show)
        {
            _peoplePage ??= new PeoplePage(this);
            PeopleHost.Content = _peoplePage;
            PeopleHost.Visibility = Visibility.Visible;
        }
        else PeopleHost.Visibility = Visibility.Collapsed;
    }

    private (string title, string sub) PeopleHeader()
    {
        var n = PeopleStore.Current?.People(id => Lib.Get(id) is Photo p && !p.IsTrashed).Count ?? 0;
        return ("People", n == 0 ? "Faces in your photos, grouped by who they are" : n == 1 ? "1 person" : $"{n:N0} people");
    }

    public void ShowPerson(string personId)
    {
        _personId = personId;
        ShowView(LibraryView.Person);
    }

    /// <summary>Opens Photos with this in search (from the Things suggestions).</summary>
    public void SearchFor(string text)
    {
        ShowView(LibraryView.Photos);
        SearchBox.Text = text;
        SearchBox.Focus();
        SearchBox.CaretIndex = text.Length;
    }

    private void PersonBanner(Action<string, Action, bool> act, out string icon, out string text)
    {
        icon = "";
        var store = PeopleStore.Current;
        var p = store?.GetPerson(_personId);
        if (p == null) { text = "This person isn't here any more."; return; }
        text = string.IsNullOrEmpty(p.Name)
            ? "Shutterbox thinks these are the same person. Add a name to find them in search. If some aren't them, select those and choose Not them."
            : $"Photos Shutterbox found {p.Name} in. Type “{p.Name}” in search to find them anywhere. If some aren't them, select those and choose Not them.";
        act(string.IsNullOrEmpty(p.Name) ? "Add a name" : "Rename", () => RenamePerson(p.Id), string.IsNullOrEmpty(p.Name));
        act("Not them", () => NotThem(p.Id), false);
        act("More…", () => ShowPersonMenu(p.Id, BannerActions.Children.OfType<FrameworkElement>().LastOrDefault()), false);
    }

    public void RenamePerson(string personId)
    {
        var store = PeopleStore.Current;
        var p = store?.GetPerson(personId);
        if (p == null) return;
        var name = InputDialog.Ask(this, string.IsNullOrEmpty(p.Name) ? "Add a name" : "Rename", "Name", "If someone already has this name, the two are put together.", p.Name ?? "");
        if (name == null) return;
        var other = store.FindByName(name);
        if (other != null && other.Id != personId && !Confirm($"“{other.Name}” is already someone. Put these photos together with theirs?", "Same person")) return;
        var kept = store.Rename(personId, name);
        store.Save();
        RefreshPeopleCount();
        if (_view == LibraryView.Person && kept != null) ShowPerson(kept.Id);
        else if (_view == LibraryView.People) _peoplePage?.Refresh();
    }

    private void NotThem(string personId)
    {
        var sel = SelectedPhotos;
        if (sel.Count == 0) { ShowToast("Select the photos that aren't them first, then choose Not them."); return; }
        PeopleStore.Current.NotThisPerson(personId, sel.Select(x => x.Id));
        PeopleStore.Current.Save();
        ClearSelection();
        ShowToast(sel.Count == 1 ? "Taken out. Shutterbox won't put that face with them again." : $"Took {sel.Count:N0} photos out. Shutterbox won't put those faces with them again.");
        if (PeopleStore.Current.GetPerson(personId) == null) ShowView(LibraryView.People); else RebuildView(true);
        RefreshPeopleCount();
    }

    public void ShowPersonMenu(string personId, FrameworkElement target)
    {
        var store = PeopleStore.Current;
        var p = store?.GetPerson(personId);
        if (p == null) return;
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string header, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Item(string.IsNullOrEmpty(p.Name) ? "Add a name…" : "Rename…", () => RenamePerson(personId));
        var others = store.People(id => Lib.Get(id) is Photo ph && !ph.IsTrashed, true).Where(v => v.Person.Id != personId && !string.IsNullOrEmpty(v.Person.Name)).Take(30).ToList();
        var same = new MenuItem { Header = "Same person as", IsEnabled = others.Count > 0 };
        foreach (var o in others)
        {
            var mi = new MenuItem { Header = o.Person.Name };
            var into = o.Person.Id;
            mi.Click += (_, _) =>
            {
                store.Merge(personId, into);
                store.Save();
                RefreshPeopleCount();
                if (_view == LibraryView.Person) ShowPerson(into); else _peoplePage?.Refresh();
            };
            same.Items.Add(mi);
        }
        menu.Items.Add(same);
        if (_view == LibraryView.Person)
            Item("Use the selected photo for their face", () =>
            {
                var sel = SelectedPhotos;
                if (sel.Count != 1) { ShowToast("Select one photo of them first."); return; }
                store.SetCover(personId, sel[0].Id);
                store.Save();
                ShowToast("Their face now comes from that photo.");
            });
        menu.Items.Add(new Separator());
        Item(p.Hidden ? "Show in People" : "Hide from People", () =>
        {
            store.SetHidden(personId, !p.Hidden);
            store.Save();
            RefreshPeopleCount();
            if (_view == LibraryView.Person) ShowView(LibraryView.People); else _peoplePage?.Refresh();
        });
        menu.IsOpen = true;
    }
}
