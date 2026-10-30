using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace DriveUp
{
    /// <summary>A driver Windows Update (the Microsoft catalog) offers for this PC.</summary>
    class DriverUpdate
    {
        public object Com;
        public string Id = "", Title = "", Model = "", Manufacturer = "", Provider = "", ClassKey = "", HardwareId = "", VersionText = "";
        public DateTime? Date;
        public long Size = -1;
        public Device Device;
        public bool Checked = true;
        /// <summary>Text shown while installing ("downloading…", "installed", an error).</summary>
        public string Status = "";
        public bool Installed, Failed;

        public string DisplayName
        {
            get { return Model.Length > 0 ? Model : Title; }
        }
    }

    class UpdateResult
    {
        public readonly List<DriverUpdate> Installed = new List<DriverUpdate>();
        public readonly List<DriverUpdate> Failed = new List<DriverUpdate>();
        public bool RebootRequired;
        public string Error;
    }

    // WUA callbacks: BeginDownload/BeginInstall want them; progress itself is polled from the job.
    // Public, so that COM can see them (non-public types are never visible to COM).
    [ComImport, Guid("8C3F1CDD-6173-4591-AEBD-A56A53CA77C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDownloadProgressChangedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args);
    }

    [ComImport, Guid("77254866-9F5B-4C8E-B9E2-C77A8530D64B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDownloadCompletedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args);
    }

    [ComImport, Guid("E01402D5-F8DA-43BA-A012-38894BD048F1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInstallationProgressChangedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args);
    }

    [ComImport, Guid("45F4F6F3-D602-4F98-9A8A-3EFA152AD2D3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInstallationCompletedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class WuCallbacks : IDownloadProgressChangedCallback, IDownloadCompletedCallback,
        IInstallationProgressChangedCallback, IInstallationCompletedCallback
    {
        void IDownloadProgressChangedCallback.Invoke(object job, object args) { }
        void IDownloadCompletedCallback.Invoke(object job, object args) { }
        void IInstallationProgressChangedCallback.Invoke(object job, object args) { }
        void IInstallationCompletedCallback.Invoke(object job, object args) { }
    }

    /// <summary>
    /// Driver updates through the Windows Update Agent: the same catalog Windows Settings uses, so
    /// everything offered is signed and meant for this hardware. Searches go to Microsoft's servers
    /// directly (not a company WSUS, which usually carries no drivers), falling back to the default.
    /// </summary>
    static class WinUpdate
    {
        static volatile bool abort;

        /// <summary>Asks Windows Update to stop the download / install that is running (it finishes or rolls back the current one).</summary>
        public static void RequestAbort() { abort = true; }

        const string Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

        static dynamic NewSession()
        {
            var t = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (t == null) throw new InvalidOperationException(L.T("Служба Windows Update недоступна", "Windows Update is not available"));
            dynamic session = Activator.CreateInstance(t);
            try { session.ClientApplicationID = "DriveUP"; } catch { }
            return session;
        }

        /// <summary>Takes from half a minute to a few minutes; call on a background (MTA) thread.</summary>
        public static List<DriverUpdate> Search()
        {
            dynamic session = NewSession();
            dynamic result;
            try
            {
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.Online = true;
                searcher.ServerSelection = 2;   // ssWindowsUpdate
                result = searcher.Search(Criteria);
            }
            catch (COMException)
            {
                // Policy may forbid going to Windows Update directly: use whatever this PC is set up for.
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.Online = true;
                result = searcher.Search(Criteria);
            }

            // 4 = failed, 5 = aborted: not the same as "nothing found".
            int rc = 2;
            try { rc = (int)result.ResultCode; } catch { }
            if (rc == 4 || rc == 5)
                throw new InvalidOperationException(L.T("Windows Update не смог завершить поиск — попробуйте позже", "Windows Update could not finish the search — try later"));

            var list = new List<DriverUpdate>();
            var seen = new HashSet<string>();
            foreach (dynamic u in result.Updates)
            {
                var d = new DriverUpdate { Com = u };
                try { d.Id = (string)u.Identity.UpdateID; } catch { }
                if (d.Id.Length > 0 && !seen.Add(d.Id)) continue;
                d.Title = Str(() => u.Title);
                d.Model = Str(() => u.DriverModel);
                d.Manufacturer = Str(() => u.DriverManufacturer);
                d.Provider = Str(() => u.DriverProvider);
                d.ClassKey = Str(() => u.DriverClass);
                d.HardwareId = Str(() => u.DriverHardwareID);
                try { d.Date = ((DateTime)u.DriverVerDate).Date; } catch { }
                try { d.Size = Convert.ToInt64(u.MaxDownloadSize); } catch { }
                d.VersionText = VersionFromTitle(d.Title);
                // BIOS / UEFI: flashed at the restart and only on mains power — never ticked by itself.
                d.Checked = !Firmware.Is(d);
                list.Add(d);
            }
            return list;
        }

        static string Str(Func<object> get)
        {
            try
            {
                var v = get();
                return v == null ? "" : v.ToString().Trim();
            }
            catch { return ""; }
        }

        /// <summary>Titles look like "Intel - Display - 31.0.101.4502": the version is the last dotted number.</summary>
        static string VersionFromTitle(string title)
        {
            var m = Regex.Matches(title ?? "", @"\d+(\.\d+){1,3}");
            return m.Count > 0 ? m[m.Count - 1].Value : "";
        }

        /// <summary>
        /// Downloads, then installs. <paramref name="progress"/>(phase, 0..1, index): phase 0 = download,
        /// 1 = install; index = the update being worked on. Needs administrator rights.
        /// </summary>
        public static UpdateResult Install(List<DriverUpdate> updates, Action<int, float, int> progress)
        {
            var result = new UpdateResult();
            dynamic session = NewSession();
            var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
            dynamic toDownload = Activator.CreateInstance(collType);
            foreach (var u in updates)
            {
                dynamic com = u.Com;
                try { if (!(bool)com.EulaAccepted) com.AcceptEula(); } catch { }
                toDownload.Add(com);
            }

            abort = false;
            // ----- download -----
            progress(0, 0, 0);
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = toDownload;
            dynamic dres = null;
            var cb = new WuCallbacks();
            dynamic job = null;
            try { job = downloader.BeginDownload(cb, cb, null); }
            catch (COMException ex)
            {
                if ((uint)ex.ErrorCode == 0x80240044u) throw;   // no rights: let the caller say so
            }
            if (job != null)
            {
                while (!(bool)job.IsCompleted)
                {
                    if (abort) { try { job.RequestAbort(); } catch { } }
                    try
                    {
                        dynamic pr = job.GetProgress();
                        progress(0, (int)pr.PercentComplete / 100f, (int)pr.CurrentUpdateIndex);
                    }
                    catch { }
                    Thread.Sleep(150);
                }
                dres = downloader.EndDownload(job);
            }
            else dres = downloader.Download();   // asynchronous download refused (old agent): in one go
            progress(0, 1, updates.Count - 1);

            dynamic toInstall = Activator.CreateInstance(collType);
            var installing = new List<DriverUpdate>();
            for (int i = 0; i < updates.Count; i++)
            {
                dynamic com = updates[i].Com;
                bool ok = false;
                try { ok = (bool)com.IsDownloaded; } catch { }
                if (ok)
                {
                    toInstall.Add(com);
                    installing.Add(updates[i]);
                }
                else
                {
                    int hr = 0;
                    try { hr = (int)dres.GetUpdateResult(i).HResult; } catch { }
                    updates[i].Failed = true;
                    updates[i].Status = L.T("Не скачалось", "Download failed") + (hr != 0 ? ": " + ErrorText(hr) : "");
                    result.Failed.Add(updates[i]);
                }
            }
            if (installing.Count == 0) return result;

            // ----- install -----
            progress(1, 0, 0);
            dynamic installer = session.CreateUpdateInstaller();
            try { installer.AllowSourcePrompts = false; } catch { }
            installer.Updates = toInstall;
            dynamic ires;
            dynamic ijob = null;
            try { ijob = installer.BeginInstall(cb, cb, null); }
            catch (COMException ex)
            {
                if ((uint)ex.ErrorCode == 0x80240044u) throw;   // no rights: let the caller say so
            }
            if (ijob != null)
            {
                while (!(bool)ijob.IsCompleted)
                {
                    if (abort) { try { ijob.RequestAbort(); } catch { } }
                    try
                    {
                        dynamic pr = ijob.GetProgress();
                        int idx = (int)pr.CurrentUpdateIndex;
                        progress(1, (int)pr.PercentComplete / 100f, updates.IndexOf(installing[Math.Max(0, Math.Min(installing.Count - 1, idx))]));
                    }
                    catch { }
                    Thread.Sleep(150);
                }
                ires = installer.EndInstall(ijob);
            }
            else ires = installer.Install();   // asynchronous install refused (old agent): in one go
            progress(1, 1, updates.Count - 1);

            try { result.RebootRequired = (bool)ires.RebootRequired; } catch { }
            for (int i = 0; i < installing.Count; i++)
            {
                var u = installing[i];
                int code = 4, hr = 0;
                try
                {
                    dynamic r = ires.GetUpdateResult(i);
                    code = (int)r.ResultCode;
                    hr = (int)r.HResult;
                    if ((bool)r.RebootRequired) result.RebootRequired = true;
                }
                catch { }
                // 2 = succeeded, 3 = succeeded with errors
                if (code == 2 || code == 3)
                {
                    u.Installed = true;
                    u.Status = L.T("Установлено", "Installed");
                    result.Installed.Add(u);
                }
                else
                {
                    u.Failed = true;
                    u.Status = L.T("Ошибка: ", "Error: ") + (hr != 0 ? ErrorText(hr) : L.T("установка не удалась", "installation failed"));
                    result.Failed.Add(u);
                }
            }
            return result;
        }

        /// <summary>Plain words for the Windows Update errors people actually meet.</summary>
        public static string ErrorText(int hr)
        {
            switch ((uint)hr)
            {
                case 0x80240044: return L.T("нужны права администратора", "administrator rights are needed");
                case 0x8024402C:
                case 0x80072EE7:
                case 0x80072EFD:
                case 0x80072EFE:
                case 0x8024401C:
                case 0x80072EE2: return L.T("нет связи с Windows Update — проверьте интернет", "cannot reach Windows Update — check the Internet connection");
                case 0x8024001E: return L.T("служба Windows Update останавливается — попробуйте ещё раз", "Windows Update is stopping — try again");
                case 0x80240016: return L.T("Windows уже устанавливает другие обновления", "Windows is already installing other updates");
                case 0x8024000B: return L.T("операция отменена", "the operation was cancelled");
                case 0x80070422: return L.T("служба Windows Update отключена", "the Windows Update service is disabled");
                case 0x80244022:
                case 0x8024401F: return L.T("сервер Windows Update недоступен — попробуйте позже", "the Windows Update server is unavailable — try later");
                case 0x80240438:
                case 0x8024500C: return L.T("доступ к Windows Update закрыт политикой", "access to Windows Update is blocked by policy");
                default: return L.T("код ", "code ") + "0x" + ((uint)hr).ToString("X8", CultureInfo.InvariantCulture);
            }
        }

        public static string ErrorText(Exception ex)
        {
            var com = ex as COMException;
            if (com != null) return ErrorText(com.ErrorCode);
            var cast = ex as InvalidCastException;
            return cast != null ? L.T("ответ Windows Update не распознан", "unexpected answer from Windows Update") : ex.Message;
        }
    }
}
