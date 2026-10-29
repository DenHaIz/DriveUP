using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DriveUp
{
    enum DownloadState { Queued, Downloading, Done, Failed }

    /// <summary>One driver from the Updates list: downloaded in the queue, installed later on the Install tab.</summary>
    class DownloadJob
    {
        public VendorUpdate Update;
        public DownloadState State;
        public long Done, Total;
        public string Error = "";
        /// <summary>The checked file: a catalog .cab, a vendor's .zip (both unpacked on the Install tab) or a vendor's installer.</summary>
        public string File;
        /// <summary>An installer's signer (O=), checked against the vendor.</summary>
        public string Signer = "";

        public bool IsInstaller
        {
            get { return File != null && File.EndsWith(".exe", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>"скачано 45 %", or the bytes so far when the size is unknown.</summary>
        public string ProgressText
        {
            get
            {
                if (Total > 0) return L.T("скачано ", "downloaded ") + (int)(Done * 100 / Total) + " %";
                return Done > 0 ? L.Size(Done) + "…" : L.T("скачивание…", "downloading…");
            }
        }
    }

    /// <summary>
    /// The download queue: drivers ticked on the Updates tab are fetched one after another (catalog .cab
    /// files from Microsoft's servers, vendors' files from their own hosts, checked as for a single install)
    /// and handed to the Install tab, where they are installed together. Events come on the UI thread.
    /// </summary>
    static class Downloads
    {
        static readonly List<DownloadJob> jobs = new List<DownloadJob>();
        static SynchronizationContext ui;
        static Thread worker;

        /// <summary>A job changed state or progress.</summary>
        public static event Action<DownloadJob> Changed;
        /// <summary>A job ended (Done or Failed).</summary>
        public static event Action<DownloadJob> Finished;
        /// <summary>Nothing is queued or downloading any more.</summary>
        public static event Action Idle;

        /// <summary>The same driver (a new check makes new objects): by catalog ID, or by the vendor's file address.</summary>
        public static bool Same(VendorUpdate a, VendorUpdate b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (a.CatalogId.Length > 0 || b.CatalogId.Length > 0) return a.CatalogId == b.CatalogId;
            return a.FileUrl.Length > 0 && a.FileUrl == b.FileUrl;
        }

        /// <summary>The job of this update, or null.</summary>
        public static DownloadJob Of(VendorUpdate v)
        {
            lock (jobs) return jobs.LastOrDefault(j => Same(j.Update, v));
        }

        public static bool Busy
        {
            get { lock (jobs) return jobs.Any(j => j.State == DownloadState.Queued || j.State == DownloadState.Downloading); }
        }

        /// <summary>Waiting or downloading now.</summary>
        public static int Pending
        {
            get { lock (jobs) return jobs.Count(j => j.State == DownloadState.Queued || j.State == DownloadState.Downloading); }
        }

        /// <summary>Can go into the queue: a catalog driver, or a vendor's file the feed names on its own host.</summary>
        public static bool Offers(VendorUpdate v)
        {
            return v != null && (v.CatalogId.Length > 0 || VendorInstall.Offers(v));
        }

        /// <summary>Queues the updates (UI thread). Already queued or downloaded ones are skipped; failed ones go again. Returns how many were added.</summary>
        public static int Add(IEnumerable<VendorUpdate> updates)
        {
            if (ui == null) ui = SynchronizationContext.Current;
            var added = new List<DownloadJob>();
            lock (jobs)
                foreach (var v in updates)
                {
                    if (!Offers(v)) continue;
                    var old = jobs.LastOrDefault(j => Same(j.Update, v));
                    if (old != null && old.State != DownloadState.Failed) continue;
                    if (old != null) jobs.Remove(old);
                    var job = new DownloadJob { Update = v };
                    jobs.Add(job);
                    added.Add(job);
                }
            foreach (var j in added) Raise(Changed, j);
            lock (jobs)
                if (added.Count > 0 && worker == null)
                {
                    worker = new Thread(Work) { IsBackground = true, Name = "download queue" };
                    worker.SetApartmentState(ApartmentState.MTA);
                    worker.Start();
                }
            return added.Count;
        }

        static void Work()
        {
            while (true)
            {
                DownloadJob job;
                lock (jobs)
                {
                    job = jobs.FirstOrDefault(j => j.State == DownloadState.Queued);
                    if (job == null)
                    {
                        worker = null;
                        break;
                    }
                    job.State = DownloadState.Downloading;
                }
                Post(() => Raise(Changed, job));
                int lastPercent = -1;
                long lastBytes = 0;
                Action<long, long> progress = (done, total) =>
                {
                    job.Done = done;
                    job.Total = total;
                    // A few updates a second at most: per percent, or per 8 MB when the size is unknown.
                    int pc = total > 0 ? (int)(done * 100 / total) : -1;
                    if (total > 0 ? pc == lastPercent : done - lastBytes < (8 << 20)) return;
                    lastPercent = pc;
                    lastBytes = done;
                    Post(() => Raise(Changed, job));
                };
                string error = null, file = null, signer = null;
                try
                {
                    if (job.Update.CatalogId.Length > 0) file = CatalogSource.Download(job.Update.CatalogId, progress);
                    else file = VendorInstall.Fetch(job.Update, progress, out signer, out error);
                }
                catch (Exception ex) { error = ex.Message; }
                job.File = file;
                job.Signer = signer ?? "";
                job.Error = error ?? "";
                job.State = file != null && error == null ? DownloadState.Done : DownloadState.Failed;
                if (job.State == DownloadState.Failed && job.Error.Length == 0) job.Error = L.T("не удалось скачать", "could not download");
                Post(() =>
                {
                    Raise(Changed, job);
                    Raise(Finished, job);
                });
            }
            Post(() => { if (!Busy && Idle != null) Idle(); });
        }

        static void Raise(Action<DownloadJob> e, DownloadJob j)
        {
            if (e != null) e(j);
        }

        static void Post(Action a)
        {
            if (ui != null) ui.Post(_ => a(), null);
            else a();
        }
    }
}
