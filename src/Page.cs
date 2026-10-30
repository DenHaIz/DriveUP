using System.Drawing;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>What a page can ask of the main window.</summary>
    interface IHost
    {
        Form Window { get; }
        void ShowToast(string text, ToastKind kind);
        void UpdateStatus();
        void BeginBusy();
        void EndBusy();
        void OpenTab(int index);
        /// <summary>False (with a hint to the user) while another install / removal / backup is running.</summary>
        bool CanStartOperation();
        /// <summary>Devices tab, optionally filtered to the devices that need attention.</summary>
        void ShowDevices(bool attention);
        /// <summary>Install tab with these folders/files loaded (restoring a backup goes this way).</summary>
        void InstallFrom(string[] paths);
    }

    /// <summary>One tab of the main window.</summary>
    abstract class Page : BufferedPanel, IScalable
    {
        /// <summary>Re-applies sizes set in design pixels and lays the page out again.</summary>
        public virtual void ApplyScale() { }

        protected readonly IHost Host;
        bool activated;

        protected Page(IHost host)
        {
            Host = host;
        }

        protected static int S(int v) { return Theme.S(v); }

        public virtual void ApplyTheme()
        {
            BackColor = Theme.Current.Bg;
            foreach (Control c in Controls)
            {
                var card = c as Card;
                if (card != null) card.ApplyTheme();
            }
        }

        public abstract void ApplyLanguage();

        /// <summary>Called every time the tab is shown; the first time also starts loading.</summary>
        public void Activate()
        {
            if (!activated)
            {
                activated = true;
                FirstActivate();
            }
            OnActivated();
        }

        /// <summary>Starts the first load in the background before the tab is opened, so it opens ready.</summary>
        public void Preload()
        {
            if (activated) return;
            activated = true;
            FirstActivate();
        }

        protected virtual void FirstActivate() { }
        protected virtual void OnActivated() { }

        public virtual string Status { get { return ""; } }
        public virtual void RefreshData() { }
        public virtual void FocusSearch() { }

        /// <summary>Runs on the UI thread and waits; ignored once the window is gone.</summary>
        protected void RunUI(System.Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) Invoke((MethodInvoker)delegate
                {
                    // Thrown inside Invoke, it would come back on the background thread and end the process.
                    try { a(); }
                    catch (System.Exception ex) { Application.OnThreadException(ex); }
                });
            }
            catch (System.ObjectDisposedException) { }
            catch (System.InvalidOperationException) { }
        }

        /// <summary>Runs on the UI thread, waits and returns the answer (false once the window is gone).</summary>
        protected bool AskUI(System.Func<bool> ask)
        {
            bool answer = false;
            RunUI(() => answer = ask());
            return answer;
        }

        /// <summary>Queues to the UI thread without waiting.</summary>
        protected void Post(System.Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)delegate { a(); });
            }
            catch (System.InvalidOperationException) { }
        }

        protected static FlatButton Button(string glyph, ButtonKind kind = ButtonKind.Normal)
        {
            return new FlatButton("", glyph, kind);
        }

        protected static void SetButton(FlatButton b, string text)
        {
            b.AltText = null;
            b.Text = text;
            b.Width = b.PreferredWidth;
        }

        /// <summary>A caption with a changing tail (a count): as wide as in the wider language, so nothing moves on a language switch.</summary>
        protected static void SetButton(FlatButton b, string ru, string en, string tail)
        {
            b.AltText = (L.En ? ru : en) + tail;
            b.Text = L.T(ru, en) + tail;
            b.Width = b.PreferredWidth;
        }

        protected static SmoothLabel Heading()
        {
            return new SmoothLabel { AutoSize = false, Font = Theme.HeadFont, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        }
    }
}
